using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SingingFish;

/// <summary>
/// Singing Fish: a vanilla mounted fish (taxidermy-fish-*) on a vanilla antler mount sings and moves when a player walks
/// up to it - once someone has picked its song.
///
/// <see cref="BEBehaviorSingingFish"/> does the singing; a JSON patch (assets/singingfish/patches/antlermount.json)
/// puts it on the antler mount block. Two Harmony prefixes, both logged and harmless if they fail:
/// <list type="bullet">
/// <item>BlockEntityDisplay.OnTesselation (BlockEntityAntlerMount does not override it): while the fish sings, the
/// mount draws only its behaviours (as BlockEntity.OnTesselation does), not the still fish - the moving one is drawn in
/// its place.</item>
/// <item>BlockAntlerMount.OnBlockInteractStart: sprint + right-click with an empty hand on a mounted fish opens the song
/// menu instead of taking the fish down (the block calls its block entity directly, so a block behaviour never gets a
/// look in).</item>
/// </list>
///
/// Songs: the two that come with the mod (its own sounds/singingfish/*.ogg), the server's own (ModData/singingfish/
/// serversongs) and players' own (ModData/singingfish/songs, uploaded to
/// the server once when picked, downloaded by whoever is near - <see cref="ClientSongStore"/>,
/// <see cref="ServerSongStore"/>). Server settings in <see cref="SingingFishConfig"/>.
/// </summary>
public sealed class SingingFishModSystem : ModSystem
{
    public const string Channel = "singingfish";
    private const string HarmonyId = "singingfish";
    private static Harmony harmony;

    /// <summary>The client's songs (its own folder, downloads). Null on a dedicated server.</summary>
    public static ClientSongStore ClientStore { get; private set; }

    /// <summary>SERVER: the settings and players' songs, for the fish's Random pick. Null on a client of a remote server.</summary>
    public static SingingFishConfig ServerConfig { get; private set; }
    public static ServerSongStore ServerStore { get; private set; }

    /// <summary>Whether the songs that come with the mod are offered: the server's setting, on the server from its file
    /// and on a client as the server last said (on until it says).</summary>
    public static bool BuiltInSongs { get; private set; } = true;

    /// <summary>CLIENT: what the server last said - the songs it holds that this player may pick (server songs and
    /// players' songs), whether this player may add songs, is an admin, and their name as the server files it.</summary>
    public static IReadOnlyList<CatalogSong> Catalog { get; private set; } = [];
    public static bool CanUpload { get; private set; } = true;
    public static bool IsAdmin { get; private set; }
    public static string OwnFolder { get; private set; } = "";

    /// <summary>CLIENT: the catalog or the settings changed (main thread) - an open song menu redraws.</summary>
    public static event Action ServerSongsChanged;

    private ICoreServerAPI sapi;
    private ServerSongStore serverStore;
    private SingingFishConfig config;
    private IServerNetworkChannel serverChannel;

    public override void Start(ICoreAPI api)
    {
        api.RegisterBlockEntityBehaviorClass("SingingFish", typeof(BEBehaviorSingingFish));
        api.Network.RegisterChannel(Channel)
            .RegisterMessageType<SetSongPacket>()
            .RegisterMessageType<SongNeedPacket>()
            .RegisterMessageType<SongChunkPacket>()
            .RegisterMessageType<SongRequestPacket>()
            .RegisterMessageType<SongRefusedPacket>()
            .RegisterMessageType<StopSongPacket>()
            .RegisterMessageType<PlaySongPacket>()
            .RegisterMessageType<FishSettingsPacket>()
            .RegisterMessageType<ServerSettingsPacket>()
            .RegisterMessageType<SongCatalogPacket>()
            .RegisterMessageType<RemoveSongPacket>()
            .RegisterMessageType<RescanSongsPacket>();
    }

    // ------------------------------------------------------------------ server

    /// <summary>
    /// SERVER: picks up a config manager's save without a restart. Integrated Mod Manager raises imm.singingfish after
    /// it writes the file; ConfigLib and ConfigKit raise &lt;manager&gt;:singingfish:config-saved - forwarding a client's
    /// save to the server BEFORE the server has written its file, then again after - so the reload waits 50 ms and is
    /// re-armed on every event (the last one is the one that counts). All settings are the server's, so the client
    /// does not listen. With no manager installed nothing raises these and this costs nothing.
    /// </summary>
    private void ListenForConfigReloads(ICoreServerAPI api)
    {
        long pending = 0;
        EventBusListenerDelegate onSaved = delegate (string eventName, ref EnumHandling handling, IAttribute data)
        {
            if (pending != 0) api.Event.UnregisterCallback(pending);
            pending = api.Event.RegisterCallback(_ => { pending = 0; ReloadConfig(api); }, 50);
        };
        api.Event.RegisterEventBusListener(onSaved, 0.5, "imm.singingfish");
        api.Event.RegisterEventBusListener(onSaved, 0.5, "configlib:singingfish:config-saved");
        api.Event.RegisterEventBusListener(onSaved, 0.5, "configkit:singingfish:config-saved");
    }

    private void ReloadConfig(ICoreServerAPI api)
    {
        var was = (config.PlayerSongs, config.MaxSongMB, config.MaxSongsPerPlayer, config.IncludeBuiltInSongs, config.RandomPlaysPlayerSongs, config.ShowEveryonesSongs);
        config = SingingFishConfig.Load(api);
        ServerConfig = config;
        BuiltInSongs = config.IncludeBuiltInSongs;
        if (config.WasClamped)
        {
            // a corrected value: ask ConfigLib / ConfigKit to re-read the file so their panel shows it (IMM cannot)
            var tree = new TreeAttribute();
            tree.SetString("domain", "singingfish");
            api.Event.PushEvent("configlib:config-reload", tree);
            api.Event.PushEvent("configkit:config-reload", tree);
        }
        var now = (config.PlayerSongs, config.MaxSongMB, config.MaxSongsPerPlayer, config.IncludeBuiltInSongs, config.RandomPlaysPlayerSongs, config.ShowEveryonesSongs);
        if (now == was) return;   // the managers forward every admin save, client ones too: nothing to say
        // who may add songs and what is listed can both have changed: every player gets their settings and the list again
        foreach (var p in api.World.AllOnlinePlayers.OfType<IServerPlayer>()) SendSettings(p);
        SendCatalog(null);
        api.Logger.Notification("[Singing Fish] Settings reloaded: player songs {0}, up to {1} MB, {2} per player, built-in songs {3}, Random plays players' songs {4}, everyone's songs shown {5}",
            config.PlayerSongs, config.MaxSongMB, config.MaxSongsPerPlayer,
            config.IncludeBuiltInSongs ? "on" : "off", config.RandomPlaysPlayerSongs ? "on" : "off", config.ShowEveryonesSongs ? "on" : "off");
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        config = SingingFishConfig.Load(api);
        ServerConfig = config;
        BuiltInSongs = config.IncludeBuiltInSongs;
        ListenForConfigReloads(api);
        serverStore = new ServerSongStore(api);
        ServerStore = serverStore;
        serverChannel = api.Network.GetChannel(Channel)
            .SetMessageHandler<SetSongPacket>(OnSetSong)
            .SetMessageHandler<StopSongPacket>(OnStopSong)
            .SetMessageHandler<PlaySongPacket>(OnPlaySong)
            .SetMessageHandler<FishSettingsPacket>(OnFishSettings)
            .SetMessageHandler<SongChunkPacket>(OnUploadChunk)
            .SetMessageHandler<SongRequestPacket>((player, p) => serverStore.OnRequest(player, p, serverChannel))
            .SetMessageHandler<RemoveSongPacket>(OnRemoveSong)
            .SetMessageHandler<RescanSongsPacket>((player, p) => serverStore.RescanSoon());
        api.Event.PlayerNowPlaying += player => { SendSettings(player); SendCatalog(player); };
        // the song folders are read off the main thread; their new list goes out from here
        api.Event.RegisterGameTickListener(_ =>
        {
            if (!serverStore.Dirty) return;
            serverStore.Dirty = false;
            SendCatalog(null);
        }, 1000);
    }

    private void SendSettings(IServerPlayer player) => serverChannel.SendPacket(new ServerSettingsPacket
    {
        IncludeBuiltInSongs = config.IncludeBuiltInSongs,
        CanUpload = config.CanUpload(player),
        IsAdmin = SingingFishConfig.IsAdmin(player),
        OwnFolder = ServerSongStore.FolderNameOf(player.PlayerName)
    }, player);

    /// <summary>Every song players may pick from the server's own store, to one player or (null) everyone. With
    /// players' songs set to Nobody only the server songs are listed.</summary>
    /// <summary>The server's song list to one player, or (null) to everyone. With ShowEveryonesSongs off each player gets
    /// their own list: the server's songs and their own (an admin still gets all of them).</summary>
    private void SendCatalog(IServerPlayer player)
    {
        if (player != null) serverChannel.SendPacket(CatalogFor(player), player);
        else if (config.ShowEveryonesSongs) serverChannel.BroadcastPacket(CatalogFor(null));
        else foreach (var p in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>()) serverChannel.SendPacket(CatalogFor(p), p);
    }

    private SongCatalogPacket CatalogFor(IServerPlayer player)
    {
        bool all = config.ShowEveryonesSongs || player == null || SingingFishConfig.IsAdmin(player);
        string me = player == null ? "" : ServerSongStore.FolderNameOf(player.PlayerName);
        return new SongCatalogPacket
        {
            Songs = serverStore.All().Where(e => e.ServerSong || config.PlayerSongsOn && (all || e.Owner == me))
                .Select(e => new CatalogSong { Id = e.Id, Name = e.Name, Owner = e.Owner }).ToList()
        };
    }

    private void OnUploadChunk(IServerPlayer player, SongChunkPacket p)
    {
        if (!config.CanUpload(player))
        {
            serverChannel.SendPacket(new SongRefusedPacket { Id = p?.Id, LangKey = RefusedUploadKey }, player);
            return;
        }
        serverStore.OnUploadChunk(player, p, config, serverChannel);
    }

    private string RefusedUploadKey => config.PlayerSongs == SingingFishConfig.Admins ? "singingfish:refused-admins" : "singingfish:refused-off";

    /// <summary>The menu's Remove: an admin may remove any song on the server, a player only one they added.</summary>
    private void OnRemoveSong(IServerPlayer player, RemoveSongPacket p)
    {
        var e = serverStore.Get(p?.Id);
        if (e == null) return;
        bool own = !e.ServerSong && e.Owner == ServerSongStore.FolderNameOf(player.PlayerName);
        if (!own && !SingingFishConfig.IsAdmin(player))
        {
            serverChannel.SendPacket(new SongRefusedPacket { Id = e.Id, LangKey = "singingfish:refused-remove" }, player);
            return;
        }
        if (serverStore.Remove(e.Id, player))
            serverChannel.SendPacket(new SongRefusedPacket { Id = e.Id, LangKey = "singingfish:removed", Arg = e.Name }, player);
    }

    private void OnSetSong(IServerPlayer player, SetSongPacket p)
    {
        if (p == null) return;
        var pos = new BlockPos(p.X, p.Y, p.Z);
        if (sapi.World.Claims.TestAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted) return;
        var fish = sapi.World.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorSingingFish>();
        if (fish == null) return;
        if (player.Entity?.Pos.SquareDistanceTo(pos.ToVec3d()) > 16 * 16) return;

        string key = p.Key ?? "";
        string name = Songs.CleanName(p.Name);
        if (key == "") name = "";
        else if (key == "random") { }
        else if (key.StartsWith(BEBehaviorSingingFish.RandomByPrefix))
        {
            // Random (my songs): only ever the picking player's own songs, and only while the server takes them
            if (key[BEBehaviorSingingFish.RandomByPrefix.Length..] != ServerSongStore.FolderNameOf(player.PlayerName)) return;
            if (!config.PlayerSongsOn)
            {
                serverChannel.SendPacket(new SongRefusedPacket { Id = "", LangKey = "singingfish:refused-off" }, player);
                return;
            }
            name = "";
        }
        else if (key.StartsWith("pack:") && key.Length < 200 && key.IndexOfAny(['\\', '\n', '\r']) < 0 && BEBehaviorSingingFish.SongAllowed(key)) { }
        else if (key.StartsWith("user:") && Songs.IsWellFormedId(key[5..]))
        {
            string id = key[5..];
            var held = serverStore.Get(id);
            if (held == null)
            {
                if (!config.CanUpload(player))
                {
                    serverChannel.SendPacket(new SongRefusedPacket { Id = id, LangKey = RefusedUploadKey }, player);
                    return;
                }
                serverChannel.SendPacket(new SongNeedPacket { Id = id }, player);
            }
            else if (!held.ServerSong && !config.PlayerSongsOn)
            {
                serverChannel.SendPacket(new SongRefusedPacket { Id = id, LangKey = "singingfish:refused-off" }, player);
                return;
            }
        }
        else return;
        fish.SetSong(key, name);
    }

    /// <summary>Anyone near the fish may stop it - it is only a song, like covering your ears for everyone.</summary>
    private void OnStopSong(IServerPlayer player, StopSongPacket p)
    {
        if (p == null) return;
        var pos = new BlockPos(p.X, p.Y, p.Z);
        if (player.Entity?.Pos.SquareDistanceTo(pos.ToVec3d()) > 16 * 16) return;
        sapi.World.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorSingingFish>()?.ForceStop();
    }

    /// <summary>Anyone near the fish may make it sing, as walking up to it would.</summary>
    private void OnPlaySong(IServerPlayer player, PlaySongPacket p)
    {
        if (p == null) return;
        var pos = new BlockPos(p.X, p.Y, p.Z);
        if (player.Entity?.Pos.SquareDistanceTo(pos.ToVec3d()) > 16 * 16) return;
        sapi.World.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorSingingFish>()?.PlayNow();
    }

    /// <summary>Volume, Motion activated and trigger range: changing the fish, so build access on its land, like picking its song.</summary>
    private void OnFishSettings(IServerPlayer player, FishSettingsPacket p)
    {
        if (p == null) return;
        var pos = new BlockPos(p.X, p.Y, p.Z);
        if (player.Entity?.Pos.SquareDistanceTo(pos.ToVec3d()) > 16 * 16) return;
        if (sapi.World.Claims.TestAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted) return;
        sapi.World.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorSingingFish>()?.SetSettings(p.Volume, p.Motion, p.Range);
    }

    // ------------------------------------------------------------------ client

    public override void StartClientSide(ICoreClientAPI capi)
    {
        BEBehaviorSingingFish.ResetSongs();
        SongLibrary.Reset();
        IClientNetworkChannel channel = null;
        ClientStore = new ClientSongStore(capi, () => channel);
        channel = capi.Network.GetChannel(Channel)
            .SetMessageHandler<SongNeedPacket>(p => ClientStore.Upload(p.Id))
            .SetMessageHandler<SongChunkPacket>(p => ClientStore.OnDownloadChunk(p))
            .SetMessageHandler<SongRefusedPacket>(p => capi.ShowChatMessage(Lang.Get(p.LangKey ?? "singingfish:refused-notogg", p.Arg ?? "")))
            .SetMessageHandler<ServerSettingsPacket>(p =>
            {
                if (p == null) return;
                BuiltInSongs = p.IncludeBuiltInSongs;
                CanUpload = p.CanUpload;
                IsAdmin = p.IsAdmin;
                OwnFolder = p.OwnFolder ?? "";
                ServerSongsChanged?.Invoke();
            })
            .SetMessageHandler<SongCatalogPacket>(p =>
            {
                Catalog = (p?.Songs ?? []).Where(s => Songs.IsWellFormedId(s?.Id)).ToList();
                ServerSongsChanged?.Invoke();
            });
        clientApi = capi;

        try
        {
            var tess = AccessTools.Method(typeof(BlockEntityDisplay), nameof(BlockEntityDisplay.OnTesselation),
                [typeof(ITerrainMeshPool), typeof(ITesselatorAPI)]);
            harmony ??= new Harmony(HarmonyId);
            // checked, not remembered: another mod removing every patch when a world closes must not leave us without it
            if (!HasPrefix(tess, HarmonyId))
                harmony.Patch(tess, prefix: new HarmonyMethod(typeof(SingingFishModSystem), nameof(OnTesselationPrefix)));
        }
        catch (Exception e)
        {
            capi.Logger.Error("[Singing Fish] Could not patch the antler mount's drawing - a singing fish will show twice: {0}", e.Message);
        }
    }

    private static ICoreClientAPI clientApi;

    public override void StartPre(ICoreAPI api)
    {
        // The interaction patch is needed on BOTH sides (the server must not take the fish down either). Once per
        // process (single player runs both sides in one) - but CHECKED on every start, not remembered: a mod that
        // removes every patch when a world closes would otherwise leave each later world taking the fish down.
        lock (typeof(SingingFishModSystem))
        {
            try
            {
                var interact = AccessTools.Method(typeof(BlockAntlerMount), nameof(BlockAntlerMount.OnBlockInteractStart),
                    [typeof(IWorldAccessor), typeof(IPlayer), typeof(BlockSelection)]);
                if (!HasPrefix(interact, InteractHarmonyId))
                    new Harmony(InteractHarmonyId).Patch(interact, prefix: new HarmonyMethod(typeof(SingingFishModSystem), nameof(InteractPrefix)));
            }
            catch (Exception e)
            {
                api.Logger.Error("[Singing Fish] Could not patch the antler mount's right-click - no song menu: {0}", e.Message);
            }
        }
    }

    private const string InteractHarmonyId = HarmonyId + ".interact";

    /// <summary>Whether our prefix (by its Harmony id) is on the method now.</summary>
    private static bool HasPrefix(MethodBase method, string owner)
        => method != null && Harmony.GetPatchInfo(method)?.Prefixes.Any(p => p.owner == owner) == true;

    /// <summary>While the fish sings, the mount draws only what its behaviours add (BlockEntity.OnTesselation).</summary>
    public static bool OnTesselationPrefix(BlockEntityDisplay __instance, ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator, ref bool __result)
    {
        if (__instance is not BlockEntityAntlerMount || __instance.GetBehavior<BEBehaviorSingingFish>() is not { HideStill: true }) return true;
        bool result = false;
        foreach (var behavior in __instance.Behaviors) result |= behavior.OnTesselation(mesher, tessThreadTesselator);
        __result = result;
        return false;
    }

    /// <summary>Sprint + right-click with an empty hand on a mounted fish: the song menu, not taking it down.</summary>
    public static bool InteractPrefix(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref bool __result)
    {
        if (!SprintHeld(byPlayer) || byPlayer.InventoryManager?.ActiveHotbarSlot?.Empty != true || blockSel == null) return true;
        var be = world.BlockAccessor.GetBlockEntity(blockSel.Position);
        var fish = be?.GetBehavior<BEBehaviorSingingFish>();
        if (fish == null || !fish.HasFish) return true;
        if (world.Side == EnumAppSide.Client && clientApi != null)
            new GuiDialogSongPicker(clientApi, blockSel.Position, fish.SongKey, fish.SongName, fish.Volume, fish.Motion, fish.Range).TryOpen();
        __result = true;
        return false;
    }

    /// <summary>
    /// The sprint key as a click modifier (CtrlKey) or as movement (Sprint), from both places the game keeps a player's
    /// controls - the entity's and the player's own - so one copy lagging behind on the server cannot let the click
    /// through to vanilla, which takes the fish down.
    /// </summary>
    private static bool SprintHeld(IPlayer byPlayer)
    {
        var a = byPlayer?.Entity?.Controls;
        var b = byPlayer?.WorldData?.EntityControls;
        return a?.CtrlKey == true || a?.Sprint == true || b?.CtrlKey == true || b?.Sprint == true;
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        ClientStore = null;
        clientApi = null;
        ServerConfig = null;
        ServerStore = null;
        BuiltInSongs = true;
        Catalog = [];
        CanUpload = true;
        IsAdmin = false;
        OwnFolder = "";
        ServerSongsChanged = null;
        BEBehaviorSingingFish.ResetSongs();
        SongLibrary.Reset();
    }
}
