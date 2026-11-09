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
/// <item>BlockAntlerMount.OnBlockInteractStart: sneak + right-click with an empty hand on a mounted fish opens the song
/// menu instead of taking the fish down (the block calls its block entity directly, so a block behaviour never gets a
/// look in).</item>
/// </list>
///
/// Songs: song packs (any domain's sounds/singingfish/*.ogg) and players' own (ModData/singingfish/songs, uploaded to
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
            .RegisterMessageType<FishSettingsPacket>();
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
        var was = (config.AllowPlayerSongs, config.MaxSongMB, config.MaxSongsPerPlayer);
        config = SingingFishConfig.Load(api);
        if (config.WasClamped)
        {
            // a corrected value: ask ConfigLib / ConfigKit to re-read the file so their panel shows it (IMM cannot)
            var tree = new TreeAttribute();
            tree.SetString("domain", "singingfish");
            api.Event.PushEvent("configlib:config-reload", tree);
            api.Event.PushEvent("configkit:config-reload", tree);
        }
        var now = (config.AllowPlayerSongs, config.MaxSongMB, config.MaxSongsPerPlayer);
        if (now == was) return;   // the managers forward every admin save, client ones too: nothing to say
        api.Logger.Notification("[Singing Fish] Settings reloaded: player songs {0}, up to {1} MB, {2} per player",
            config.AllowPlayerSongs ? "allowed" : "off", config.MaxSongMB, config.MaxSongsPerPlayer);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        config = SingingFishConfig.Load(api);
        ListenForConfigReloads(api);
        serverStore = new ServerSongStore(api);
        serverChannel = api.Network.GetChannel(Channel)
            .SetMessageHandler<SetSongPacket>(OnSetSong)
            .SetMessageHandler<StopSongPacket>(OnStopSong)
            .SetMessageHandler<PlaySongPacket>(OnPlaySong)
            .SetMessageHandler<FishSettingsPacket>(OnFishSettings)
            .SetMessageHandler<SongChunkPacket>((player, p) => serverStore.OnUploadChunk(player, p, config, serverChannel))
            .SetMessageHandler<SongRequestPacket>((player, p) => serverStore.OnRequest(player, p, serverChannel));
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
        else if (key.StartsWith("pack:") && key.Length < 200 && key.IndexOfAny(['\\', '\n', '\r']) < 0) { }
        else if (key.StartsWith("user:") && Songs.IsWellFormedId(key[5..]))
        {
            string id = key[5..];
            if (!serverStore.Has(id))
            {
                if (!config.AllowPlayerSongs)
                {
                    serverChannel.SendPacket(new SongRefusedPacket { Id = id, LangKey = "singingfish:refused-off" }, player);
                    return;
                }
                serverChannel.SendPacket(new SongNeedPacket { Id = id }, player);
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

    /// <summary>Volume and Motion activated: changing the fish, so build access on its land, like picking its song.</summary>
    private void OnFishSettings(IServerPlayer player, FishSettingsPacket p)
    {
        if (p == null) return;
        var pos = new BlockPos(p.X, p.Y, p.Z);
        if (player.Entity?.Pos.SquareDistanceTo(pos.ToVec3d()) > 16 * 16) return;
        if (sapi.World.Claims.TestAccess(player, pos, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted) return;
        sapi.World.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorSingingFish>()?.SetSettings(p.Volume, p.Motion);
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
            .SetMessageHandler<SongRefusedPacket>(p => capi.ShowChatMessage(Lang.Get(p.LangKey ?? "singingfish:refused-notogg", p.Arg ?? "")));
        clientApi = capi;

        if (harmony != null) return;
        try
        {
            harmony = new Harmony(HarmonyId);
            var tess = AccessTools.Method(typeof(BlockEntityDisplay), nameof(BlockEntityDisplay.OnTesselation),
                [typeof(ITerrainMeshPool), typeof(ITesselatorAPI)]);
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
        // The interaction patch is needed on BOTH sides (the server must not take the fish down either). Patched once
        // per process - single player runs both sides in one.
        lock (typeof(SingingFishModSystem))
        {
            if (interactPatched) return;
            interactPatched = true;
        }
        try
        {
            var h = new Harmony(HarmonyId + ".interact");
            var interact = AccessTools.Method(typeof(BlockAntlerMount), nameof(BlockAntlerMount.OnBlockInteractStart),
                [typeof(IWorldAccessor), typeof(IPlayer), typeof(BlockSelection)]);
            h.Patch(interact, prefix: new HarmonyMethod(typeof(SingingFishModSystem), nameof(InteractPrefix)));
        }
        catch (Exception e)
        {
            api.Logger.Error("[Singing Fish] Could not patch the antler mount's right-click - no song menu: {0}", e.Message);
        }
    }

    private static bool interactPatched;

    /// <summary>While the fish sings, the mount draws only what its behaviours add (BlockEntity.OnTesselation).</summary>
    public static bool OnTesselationPrefix(BlockEntityDisplay __instance, ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator, ref bool __result)
    {
        if (__instance is not BlockEntityAntlerMount || __instance.GetBehavior<BEBehaviorSingingFish>() is not { HideStill: true }) return true;
        bool result = false;
        foreach (var behavior in __instance.Behaviors) result |= behavior.OnTesselation(mesher, tessThreadTesselator);
        __result = result;
        return false;
    }

    /// <summary>Sneak + right-click with an empty hand on a mounted fish: the song menu, not taking it down.</summary>
    public static bool InteractPrefix(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref bool __result)
    {
        if (byPlayer?.Entity?.Controls.ShiftKey != true || byPlayer.InventoryManager?.ActiveHotbarSlot?.Empty != true || blockSel == null) return true;
        var be = world.BlockAccessor.GetBlockEntity(blockSel.Position);
        var fish = be?.GetBehavior<BEBehaviorSingingFish>();
        if (fish == null || !fish.HasFish) return true;
        if (world.Side == EnumAppSide.Client && clientApi != null)
            new GuiDialogSongPicker(clientApi, blockSel.Position, fish.SongKey, fish.SongName, fish.Volume, fish.Motion).TryOpen();
        __result = true;
        return false;
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        ClientStore = null;
        clientApi = null;
        BEBehaviorSingingFish.ResetSongs();
        SongLibrary.Reset();
    }
}
