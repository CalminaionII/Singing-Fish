using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SingingFish;

/// <summary>
/// On a vanilla antler mount: when it holds a mounted fish, the fish sings - when a player walks up (Motion activated,
/// the default) or when someone presses Play in its song menu.
///
/// WHAT it sings is chosen per fish in the song menu (sprint + right-click with an empty hand): nothing (the default - a
/// fish sings only once someone has picked a song), Random, Random (my songs), one of the two songs that come with
/// the mod, one of the server's own songs, or a player's own song (from their songs folder, uploaded to the server once and downloaded by whoever is near). The menu
/// also sets the fish's volume, trigger range and whether it is motion activated, and has Play and Stop singing.
///
/// SERVER decides when: every half second, if motion activated, it looks for players within the fish's trigger range
/// (set in the menu, <see cref="MinRange"/> - <see cref="MaxRange"/> blocks, <see cref="DefaultRange"/> by default);
/// when one arrives who was not there on the last look, and the fish is not still busy with the last song, it bumps a
/// counter and syncs it. Play does the same at once. For Random the SERVER also picks the song each time (from the
/// mod's songs, the server's songs and, when the server takes them, every player's song on it) and syncs it, so everyone hears the same one
/// and players' songs are in the draw. Standing still in front of it does not repeat
/// the song - walk away and back.
///
/// CLIENTS act on a new counter: the song is decoded in the background (SongLibrary - sound and mouth timing), played
/// straight through OpenAL (SongPlayer), and the still fish (drawn by the antler mount into the chunk) is swapped for a
/// moving copy (FishRig) whose head and jaw are turned every frame from the song's timing. When the song ends the still
/// fish comes back. A counter already there when the mount loads is not sung, so joining a world does not set every
/// fish off. It keeps singing while the game is paused.
/// </summary>
public sealed class BEBehaviorSingingFish : BlockEntityBehavior
{
    /// <summary>Trigger range limits and default, in blocks. Kept at 16 or less: the song fades out by 32 blocks, so a
    /// fish set off from further away would hardly be heard (16 is also how far away the menu's Play works).</summary>
    public const int MinRange = 1, MaxRange = 16, DefaultRange = 4;
    /// <summary>How long the server waits before the fish may start again (the server does not know the song's length).</summary>
    private const int BusyMs = 20000;
    private const int MaxSongMs = 7 * 60 * 1000;
    private const float HeadOut = 30, JawOpen = 25, TailOut = 35;

    // synced
    private int seq;
    /// <summary>For Random: the song the server picked for the latest sing, as a "pack:" / "user:" key ("pack:" = one
    /// of the songs that come with the mod - the name is kept from when other mods could add songs, so fish keep them).</summary>
    private string playKey = "";
    /// <summary>Bumped by Stop singing: every client stops this fish's song when it changes.</summary>
    private int stopSeq;
    /// <summary>"" no song (default), "random", "randomby:&lt;player folder&gt;" (Random from one player's songs),
    /// "pack:&lt;asset location&gt;", "user:&lt;id&gt;".</summary>
    private string songKey = "";

    /// <summary>The key for Random from one player's own songs on the server: this prefix + their folder name.</summary>
    public const string RandomByPrefix = "randomby:";

    /// <summary>Either Random: the server picks the song each time.</summary>
    public static bool IsRandomKey(string key) => key == "random" || key.StartsWith(RandomByPrefix);
    private string songName = "";
    private float volume = 1f;
    private bool motion = true;
    private float range = DefaultRange;

    public string SongKey => songKey;
    public string SongName => songName;
    public float Volume => volume;
    public bool Motion => motion;
    public float Range => range;

    // server
    private long busyUntil;
    private HashSet<string> near = new();

    // client
    private int seenSeq = int.MinValue, seenStop = int.MinValue;
    private FishRig rig;
    private string rigFor;
    private double headSide = 1, jawSign = -1;
    private SongPlayer player;
    private SongData playing;
    private List<(float from, float to)> headOutTimes;
    private float[] randomFlicks;
    private int nextFlick, lastFrame;
    private long singStart, stopAt;
    private bool singing, stopping;
    private float headNow, jawNow, tailNow, tailKick;
    /// <summary>A sing that is waiting for its song: a player song downloading (pendingId) or decoding (pendingKey).</summary>
    private string pendingId, pendingKey;
    private long pendingSince;

    /// <summary>The antler mount must not draw its still fish now (SingingFishModSystem's patch).</summary>
    public bool HideStill => singing;

    public BEBehaviorSingingFish(BlockEntity blockentity) : base(blockentity) { }

    public override void Initialize(ICoreAPI api, JsonObject properties)
    {
        base.Initialize(api, properties);
        if (api.Side == EnumAppSide.Server) Blockentity.RegisterGameTickListener(ServerTick, 500);
        else
        {
            seenSeq = seq;
            seenStop = stopSeq;
            Blockentity.RegisterGameTickListener(ClientTick, 100);
            if (SingingFishModSystem.ClientStore is { } store) store.Arrived += OnSongArrived;
            SongLibrary.Ready += OnSongReady;
            Prefetch();
        }
    }

    public bool HasFish => Fish != null;

    private ItemStack Fish =>
        (Blockentity as BlockEntityContainer)?.Inventory is { Count: > 0 } inv && inv[0]?.Itemstack is { } s
        && s.Collectible?.Code?.Path?.StartsWith("taxidermy-fish-") == true ? s : null;

    // ------------------------------------------------------------------ server

    private void ServerTick(float dt)
    {
        if (Fish == null || songKey.Length == 0 || !motion) { near.Clear(); return; }
        var centre = Pos.ToVec3d().Add(0.5, 0.5, 0.5);
        var now = new HashSet<string>();
        bool arrived = false;
        foreach (var p in Api.World.GetPlayersAround(centre, range, range))
        {
            if (p?.Entity == null || !p.Entity.Alive || p.WorldData?.CurrentGameMode == EnumGameMode.Spectator) continue;
            now.Add(p.PlayerUID);
            if (!near.Contains(p.PlayerUID)) arrived = true;
        }
        near = now;
        if (arrived && Api.World.ElapsedMilliseconds >= busyUntil) Sing();
    }

    private void Sing()
    {
        if (IsRandomKey(songKey))
        {
            var pool = RandomPool();
            if (pool.Count == 0) return;
            // not the same song twice running when there is a choice
            if (pool.Count > 1) pool.Remove(playKey);
            playKey = pool[Api.World.Rand.Next(pool.Count)];
        }
        else if (!SongAllowed(songKey) || PlayerSongSilenced(songKey)) return;
        seq++;
        busyUntil = Api.World.ElapsedMilliseconds + BusyMs;
        Blockentity.MarkDirty(false);
    }

    /// <summary>SERVER: what Random may pick - the mod's own songs (while the server includes them), every server song, and every player's song on
    /// the server while players' songs are on and Random may play them. Random (my songs) picks only from the songs
    /// that one player added to the server (none while the server takes no players' songs; RandomPlaysPlayerSongs is
    /// about plain Random and does not apply - the player chose their own songs).</summary>
    private List<string> RandomPool()
    {
        var config = SingingFishModSystem.ServerConfig;
        if (songKey.StartsWith(RandomByPrefix))
        {
            string owner = songKey[RandomByPrefix.Length..];
            if (SingingFishModSystem.ServerStore is not { } held || config is not { PlayerSongsOn: true }) return [];
            return held.All().Where(e => !e.ServerSong && e.Owner == owner).Select(e => "user:" + e.Id).ToList();
        }
        var pool = ModSongs(Api).Where(ModSongAllowed).Select(l => "pack:" + l).ToList();
        if (SingingFishModSystem.ServerStore is { } store)
        {
            bool players = config is { PlayerSongsOn: true, RandomPlaysPlayerSongs: true };
            pool.AddRange(store.All().Where(e => e.ServerSong || players).Select(e => "user:" + e.Id));
        }
        return pool;
    }

    /// <summary>The songs that come with the mod are its own domain's; a server can leave them out.</summary>
    public static bool IsBuiltIn(AssetLocation loc) => loc?.Domain == "singingfish";

    /// <summary>Whether this "pack:" song may be sung here: only the mod's own songs (song packs from other mods are no
    /// longer read - the server songs folder does that job), and only while the server includes them.</summary>
    public static bool ModSongAllowed(AssetLocation loc) => IsBuiltIn(loc) && SingingFishModSystem.BuiltInSongs;

    /// <summary>A fish's picked song may still be sung (a built-in one the server has since left out may not).</summary>
    public static bool SongAllowed(string key) =>
        !key.StartsWith("pack:") || ModSongAllowed(new AssetLocation(key[5..]));

    /// <summary>SERVER: a fish set to a song a player added stays quiet while the server takes no players' songs
    /// (PlayerSongs Nobody hides them from the menu and Random; this stops fish already set to one). Server songs are
    /// not affected.</summary>
    private static bool PlayerSongSilenced(string key) =>
        key.StartsWith("user:") && SingingFishModSystem.ServerConfig is { PlayerSongsOn: false }
        && SingingFishModSystem.ServerStore?.Get(key[5..]) is { ServerSong: false };

    /// <summary>SERVER: the menu's Play - sings now, motion or not (needs a song picked).</summary>
    public void PlayNow()
    {
        if (Fish == null || songKey.Length == 0) return;
        Sing();
    }

    /// <summary>
    /// SERVER: the menu's Stop singing. Clients stop at once; the fish may start again a few seconds later when someone
    /// walks up (not straight away for whoever pressed it - they are still standing there, so it needs a fresh arrival).
    /// </summary>
    public void ForceStop()
    {
        stopSeq++;
        busyUntil = Api.World.ElapsedMilliseconds + 3000;
        Blockentity.MarkDirty(false);
    }

    /// <summary>SERVER: the menu's pick (already checked by the mod system).</summary>
    public void SetSong(string key, string name)
    {
        songKey = key ?? "";
        songName = name ?? "";
        Blockentity.MarkDirty(false);
    }

    /// <summary>SERVER: the menu's volume, Motion activated and trigger range (0 or less: range left as it is).</summary>
    public void SetSettings(float newVolume, bool newMotion, float newRange)
    {
        volume = float.IsFinite(newVolume) ? Math.Clamp(newVolume, 0f, 1f) : 1f;
        motion = newMotion;
        if (float.IsFinite(newRange) && newRange > 0) range = Math.Clamp(newRange, MinRange, MaxRange);
        Blockentity.MarkDirty(false);
    }

    // ------------------------------------------------------------------ sync

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetInt("singingFishSeq", seq);
        tree.SetString("singingFishPlayKey", playKey);
        tree.SetInt("singingFishStop", stopSeq);
        tree.SetString("singingFishSong", songKey);
        tree.SetString("singingFishSongName", songName);
        tree.SetFloat("singingFishVolume", volume);
        tree.SetBool("singingFishMotion", motion);
        tree.SetFloat("singingFishRange", range);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        seq = tree.GetInt("singingFishSeq");
        playKey = tree.GetString("singingFishPlayKey", "");
        stopSeq = tree.GetInt("singingFishStop");
        songKey = tree.GetString("singingFishSong", "");
        songName = tree.GetString("singingFishSongName", "");
        volume = Math.Clamp(tree.GetFloat("singingFishVolume", 1f), 0f, 1f);
        motion = tree.GetBool("singingFishMotion", true);
        float r = tree.GetFloat("singingFishRange", DefaultRange);
        range = float.IsFinite(r) ? Math.Clamp(r, MinRange, MaxRange) : DefaultRange;
        if (Api is not ICoreClientAPI capi) return;
        Prefetch();
        if (seenStop != int.MinValue && stopSeq != seenStop)
        {
            seenStop = stopSeq;
            pendingId = pendingKey = null;
            capi.Event.EnqueueMainThreadTask(() => StopSinging(), "singingfish-stop");
        }
        if (seenSeq != int.MinValue && seq != seenSeq)
        {
            seenSeq = seq;
            capi.Event.EnqueueMainThreadTask(StartSinging, "singingfish-start");
        }
    }

    // ------------------------------------------------------------------ client: which song

    private static List<AssetLocation> modSongs;

    internal static void ResetSongs() => modSongs = null;

    /// <summary>The songs that come with the mod (its own sounds/singingfish/*.ogg - other mods' are not read), the same
    /// order everywhere. Sounds are a universal asset category, so the server has them too (Random is picked there).</summary>
    public static IReadOnlyList<AssetLocation> ModSongs(ICoreAPI api)
    {
        if (modSongs != null) return modSongs;
        return modSongs = api.Assets.GetMany("sounds/singingfish/", "singingfish", false)
            .Select(a => a.Location).Where(l => l.Path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.ToString(), StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// This fish's song as a key for the song library and where its bytes are - (null, null) when nothing is picked, a
    /// "pack:" song is not one of the mod's own, or a player song is not on this computer yet (then it is asked for).
    /// </summary>
    private (string key, Func<byte[]> bytes) SongSource(ICoreClientAPI capi)
    {
        string key = CurrentKey;
        if (key.StartsWith("pack:"))
        {
            var loc = new AssetLocation(key[5..]);
            if (!ModSongs(capi).Contains(loc)) return (null, null);
            return ("pack:" + loc, () => capi.Assets.TryGet(loc)?.Data);
        }
        if (key.StartsWith("user:") && SingingFishModSystem.ClientStore is { } store)
        {
            string id = key[5..];
            if (!store.Has(id)) { store.Request(id); return (null, null); }
            return ("user:" + id, () => store.Bytes(id));
        }
        return (null, null);
    }

    /// <summary>
    /// As soon as the fish is known: a player song it sings is fetched, and its song decoded (sound + mouth timing) in
    /// the background, so all of it is ready when someone walks up.
    /// </summary>
    private void Prefetch()
    {
        if (Api is not ICoreClientAPI capi || IsRandomKey(songKey)) return;
        var (key, bytes) = SongSource(capi);
        if (key != null) SongLibrary.Get(capi, key, bytes);
    }

    /// <summary>The song this fish sings now: its pick, or for Random the server's latest pick.</summary>
    private string CurrentKey => IsRandomKey(songKey) ? playKey : songKey;

    private void OnSongArrived(string id)
    {
        if (Api is not ICoreClientAPI capi) return;
        if (songKey == "user:" + id) Prefetch();          // decode it now
        if (pendingId != id) return;
        pendingId = null;
        if (capi.World.ElapsedMilliseconds - pendingSince < 20000) StartSinging();
    }

    private void OnSongReady(string key)
    {
        if (pendingKey != key || Api == null) return;
        pendingKey = null;
        if (Api.World.ElapsedMilliseconds - pendingSince < 20000) StartSinging();
    }

    // ------------------------------------------------------------------ client: singing

    private void StartSinging()
    {
        if (singing || Api is not ICoreClientAPI capi) return;
        var fish = Fish;
        if (fish?.Item == null) return;
        var (key, bytes) = SongSource(capi);
        if (key == null)
        {
            if (CurrentKey.StartsWith("user:")) { pendingId = CurrentKey[5..]; pendingSince = capi.World.ElapsedMilliseconds; }
            return;
        }
        var song = SongLibrary.Get(capi, key, bytes);
        if (song == null) { pendingKey = key; pendingSince = capi.World.ElapsedMilliseconds; return; }   // decoding - sings when ready

        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!EnsureRig(capi, fish)) return;
            long rigMs = watch.ElapsedMilliseconds;
            var shift = RecessShift(capi);
            rig.CustomTransform = MountMatrix(fish, shift);
            playing = song;
            headOutTimes = HeadSchedule(key, song.Seconds);
            randomFlicks = FlickSchedule(key, song.Seconds);
            nextFlick = 0;
            lastFrame = -1;
            headNow = jawNow = tailNow = tailKick = 0;
            singStart = capi.World.ElapsedMilliseconds;
            singing = true;
            stopping = false;
            rig.Visible = true;
            var at = Pos.ToVec3d().Add(0.5, 0.5, 0.5);
            if (shift != null) at.Add(shift.X, shift.Y, shift.Z);
            player = SongPlayer.Play(capi, song, at, volume);
            capi.World.BlockAccessor.MarkBlockDirty(Pos);
            if (watch.ElapsedMilliseconds > 40)
                capi.Logger.Notification("[Singing Fish] Starting {0} took {1} ms on the main thread (the fish's model {2} ms)", key, watch.ElapsedMilliseconds, rigMs);
        }
        catch (Exception e)
        {
            capi.Logger.Warning("[Singing Fish] Could not start singing at {0}: {1}", Pos, e.Message);
            StopSinging(true);
        }
    }

    /// <summary>When the head is turned out during the song: random, seeded by the song, so every player sees the same.</summary>
    private static List<(float, float)> HeadSchedule(string key, float seconds)
    {
        uint h = 2166136261;
        foreach (char c in key) { h ^= c; h *= 16777619; }
        var rng = new Random((int)h);
        var list = new List<(float, float)>();
        float t = 0.15f + (float)rng.NextDouble() * 0.3f;
        while (t < seconds - 0.5f)
        {
            float outFor = 1.5f + (float)rng.NextDouble() * 4.5f;      // out for 1.5 - 6 s
            list.Add((t, Math.Min(seconds, t + outFor)));
            t += outFor + 0.5f + (float)rng.NextDouble();               // back for 0.5 - 1.5 s
        }
        return list;
    }

    /// <summary>Tail flicks for a song without usable beats: roughly every one to two seconds, seeded by the song.</summary>
    private static float[] FlickSchedule(string key, float seconds)
    {
        uint h = 2166136261;
        foreach (char c in key) { h ^= c; h *= 16777619; }
        var rng = new Random((int)(h ^ 0x5bd1e995));
        var list = new List<float>();
        for (float t = 0.8f + (float)rng.NextDouble(); t < seconds; t += 1f + (float)rng.NextDouble() * 1.2f) list.Add(t);
        return list.ToArray();
    }

    /// <summary>
    /// Every frame (FishRig): front-half turn, jaw and tail, eased. With the song's timing the head follows its singing
    /// (out while it sings, back in the gaps) and the tail flicks on its strong beats; a song without a usable answer
    /// for either uses random timings for that part. The mouth always follows the song.
    /// </summary>
    private (float head, float jaw, float tail) Drive(float dt)
    {
        if (Api is not ICoreClientAPI capi || playing == null) return (0, 0, 0);
        float t = (capi.World.ElapsedMilliseconds - singStart) / 1000f;
        var timing = playing.Timing;
        int f = (int)(t * SongTiming.Fps);
        float headTarget = 0, jawTarget = 0;
        if (!stopping)
        {
            headTarget = timing?.HeadOut != null
                ? (f < timing.Frames && timing.HeadOut[f] ? 1 : 0)
                : (headOutTimes.Any(r => t >= r.from && t < r.to) ? 1 : 0);
            if (timing != null) jawTarget = f < timing.Frames ? timing.Mouth[f] : 0;
            else jawTarget = Math.Max(0, MathF.Sin(t * MathF.PI * 2 * 3));   // no timing (song too short): chatter

            // a flick on every beat passed since the last frame (or the next random time)
            bool flick = false;
            if (timing?.TailHits != null)
            {
                for (int k = Math.Max(0, lastFrame + 1); k <= f && k < timing.Frames; k++) flick |= timing.TailHits[k];
            }
            else
            {
                while (nextFlick < randomFlicks.Length && randomFlicks[nextFlick] <= t) { flick = true; nextFlick++; }
            }
            if (flick) tailKick = 1;
        }
        lastFrame = f;
        headNow += (headTarget - headNow) * Math.Min(1, dt * 9);
        jawNow += (jawTarget - jawNow) * Math.Min(1, dt * 28);
        // a flick snaps out and falls back: the kick decays, the tail follows it quickly
        tailKick = Math.Max(0, tailKick - dt * 4f);
        tailNow += (tailKick - tailNow) * Math.Min(1, dt * 22);
        return ((float)(HeadOut * headSide * headNow), (float)(JawOpen * jawSign * jawNow), (float)(-TailOut * headSide * tailNow));
    }

    private void ClientTick(float dt)
    {
        if (!singing || Api is not ICoreClientAPI capi) return;
        player?.Update(volume);
        long ms = capi.World.ElapsedMilliseconds;
        if (stopping)
        {
            if (ms - stopAt > 450) StopSinging(true);
            return;
        }
        bool done = Fish == null
            || (player == null ? ms - singStart > (long)(playing.Seconds * 1000) + 300 : (!player.Playing && ms - singStart > 500))
            || ms - singStart > MaxSongMs;
        if (done) StopSinging();
    }

    /// <summary>Stops the song; the head and jaw ease back for a moment before the still fish returns (now = at once).</summary>
    private void StopSinging(bool now = false)
    {
        player?.Dispose();
        player = null;
        if (!singing) return;
        if (!now && rig != null)
        {
            if (!stopping) { stopping = true; stopAt = Api.World.ElapsedMilliseconds; }
            return;
        }
        if (rig != null) rig.Visible = false;
        playing = null;
        singing = stopping = false;
        Api?.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    // ------------------------------------------------------------------ client: the moving fish

    /// <summary>The fish item's own model as a rig (FishRig): built once per fish type on this mount.</summary>
    private bool EnsureRig(ICoreClientAPI capi, ItemStack fish)
    {
        string key = "singingfish-rig-" + fish.Collectible.Code;
        if (rig != null && rigFor == key) return true;
        rig?.Dispose();
        rig = null;

        var shapeBase = fish.Item.Shape?.Base;
        if (shapeBase == null) return false;
        var path = shapeBase.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
        var shape = Shape.TryGet(capi, path);
        if (shape == null)
        {
            capi.Logger.Warning("[Singing Fish] No model for {0} at {1}", fish.Collectible.Code, path);
            return false;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Walk(ShapeElement[] els) { foreach (var e in els ?? []) { if (e.Name != null) names.Add(e.Name); Walk(e.Children); } }
        Walk(shape.Elements);

        headSide = OutSide(fish);
        var (jaw, axis, sign) = FindJaw(shape, names);
        if (JawOpensOtherWay.Contains(path.Path.Split('/').Last().Replace(".json", ""))) sign = -sign;
        jawSign = sign;
        // the tail end only: "Rear" (the last body piece and its fin) on vanilla fish; the middle section only if a fish has no Rear
        string tail = names.Contains("Rear") ? "Rear" : names.Contains("Midsection") ? "Midsection" : null;
        // the fish's own animations are dropped (nothing else to work out); a do-nothing loop keeps the animator working
        shape.Animations = tail == null ? [] : [IdleLoop(tail)];
        rig = FishRig.Create(capi, key, shape, new FishTextures(capi, fish.Item, shape), Pos.ToVec3d(),
            names.Contains("Head") ? "Head" : null, jaw, axis, tail);
        rig.Drive = Drive;
        rigFor = key;
        return true;
    }

    /// <summary>A loop that moves nothing (FishRig.TailAnim): it only keeps the animator recomputing every frame.</summary>
    private static Animation IdleLoop(string part)
    {
        AnimationKeyFrame Key(int frame) => new()
        {
            Frame = frame,
            Elements = new() { [part] = new AnimationKeyFrameElement { RotationX = 0, RotationY = 0, RotationZ = 0 } }
        };
        return new Animation
        {
            Code = FishRig.TailAnim, Name = FishRig.TailAnim, QuantityFrames = 2, OnAnimationEnd = EnumEntityAnimationEndHandling.Repeat,
            KeyFrames = [Key(0), Key(1)]
        };
    }

    /// <summary>
    /// Which way along the fish's own Z is out of the plaque: +X in the antler mount's frame before its own turn (the
    /// plaque on the west edge, facing +X), taken back into the fish's frame through its onAntlerMountTransform. A head
    /// at -X turned by +a about Y moves towards +Z.
    /// </summary>
    private static double OutSide(ItemStack fish)
    {
        var t = fish.Collectible.Attributes?["onAntlerMountTransform"].AsObject<ModelTransform>();
        if (t == null) return 1;
        t.EnsureDefaultValues();
        var r = Mat4f.Create();
        Mat4f.RotateByXYZ(r, t.Rotation.X * GameMath.DEG2RAD, t.Rotation.Y * GameMath.DEG2RAD, t.Rotation.Z * GameMath.DEG2RAD);
        double outZ = r[8];                      // (R^T * +X).z = R[0][2]
        return Math.Abs(outZ) > 0.3 ? Math.Sign(outZ) : 1;
    }

    private static readonly string[] JawNames = ["Jaw", "JawLower", "Jaw2", "mouth", "Mouth"];

    /// <summary>
    /// Fish models whose jaw opens UP with FindJaw's direction (mostly jaws hung under a top lip, hinged at their back),
    /// so it is turned the other way. Shape file names, without ".json". Seen in game: barracudas, sheatfish, arapaima,
    /// juvenile sturgeon, adult largemouth bass; the pike has the barracudas' build exactly (not seen).
    /// </summary>
    private static readonly HashSet<string> JawOpensOtherWay = new(StringComparer.Ordinal)
    {
        "barracuda-adult", "barracuda-juvenile", "pike-adult", "pike-juvenile", "sheatfish-adult", "sheatfish-juvenile",
        "arapaima-adult", "arapaima-juvenile", "sturgeon-juvenile", "bass-adult",
    };

    /// <summary>
    /// The fish's jaw, the axis it opens about (0 X, 1 Y, 2 Z) and which way, from the fish's OWN "eat" animation: most
    /// vanilla fish call it Jaw and open it with negative Z, but the arapaima has Jaw2, the carp and sheatfish JawLower,
    /// the sturgeon mouth (opened with +Z). The eat keyframe that moves a jaw part furthest decides. No eat: the first
    /// jaw part found, negative Z. No jaw part: null, and the mouth stays shut.
    /// </summary>
    private static (string name, int axis, double sign) FindJaw(Shape shape, HashSet<string> names)
    {
        string best = null; int bestAxis = 2; double bestSign = -1, bestAbs = 0;
        var eat = shape.Animations?.FirstOrDefault(a => a.Code == "eat");
        foreach (var kf in eat?.KeyFrames ?? [])
            foreach (var (el, v) in kf.Elements ?? new Dictionary<string, AnimationKeyFrameElement>())
            {
                if (!JawNames.Contains(el) || !names.Contains(el)) continue;
                foreach (var (val, axis) in new[] { (v.RotationX, 0), (v.RotationY, 1), (v.RotationZ, 2) })
                {
                    if (val is not double d || Math.Abs(d) <= bestAbs) continue;
                    bestAbs = Math.Abs(d); best = el; bestAxis = axis; bestSign = Math.Sign(d);
                }
            }
        if (best != null) return (best, bestAxis, bestSign);
        return (JawNames.FirstOrDefault(names.Contains), 2, -1);
    }

    /// <summary>
    /// Where the antler mount draws its item: the item's onAntlerMountTransform (MeshData.ModelTransform's order), then
    /// the mount's turn about the block centre (BlockEntityAntlerMount.genTransformationMatrices).
    /// </summary>
    private float[] MountMatrix(ItemStack fish, Vec3f shift)
    {
        var m = Mat4f.Create();
        if (shift != null) Mat4f.Translate(m, shift.X, shift.Y, shift.Z);
        float angle = (Blockentity as BlockEntityAntlerMount)?.MeshAngleRad ?? 0;
        Mat4f.Translate(m, 0.5f, 0, 0.5f);
        Mat4f.RotateY(m, m, angle - GameMath.PIHALF);
        Mat4f.Translate(m, -0.5f, 0, -0.5f);

        var t = fish.Collectible.Attributes?["onAntlerMountTransform"].AsObject<ModelTransform>();
        if (t != null)
        {
            t.EnsureDefaultValues();
            Mat4f.Translate(m, t.Translation.X + t.Origin.X, t.Translation.Y + t.Origin.Y, t.Translation.Z + t.Origin.Z);
            Mat4f.RotateByXYZ(m, t.Rotation.X * GameMath.DEG2RAD, t.Rotation.Y * GameMath.DEG2RAD, t.Rotation.Z * GameMath.DEG2RAD);
            Mat4f.Scale(m, t.ScaleXYZ.X, t.ScaleXYZ.Y, t.ScaleXYZ.Z);
            Mat4f.Translate(m, -t.Origin.X, -t.Origin.Y, -t.Origin.Z);
        }
        return m;
    }

    /// <summary>
    /// How far Chiseled Wall Mounting (modid chiseledwallmounting) has moved this mount into a chiseled recess, in blocks,
    /// or null. That mod shifts the mount's chunk mesh when it is drawn, so the still fish sits in the recess, but our
    /// singing fish is drawn by itself and would appear where a mount on a plain wall would be. Its client cache is public
    /// (ChiseledWallMounting.ClientMountCache.TryGet(BlockPos, out RecessedMountRecord), record.Translation); it is read by
    /// reflection so the mod is not needed, and anything unexpected just means no shift.
    /// </summary>
    private Vec3f RecessShift(ICoreClientAPI capi)
    {
        try
        {
            var mod = capi.ModLoader.GetMod("chiseledwallmounting");
            var asm = mod?.Systems?.FirstOrDefault()?.GetType().Assembly;
            var tryGet = asm?.GetType("ChiseledWallMounting.ClientMountCache")?.GetMethod("TryGet", [typeof(BlockPos), asm.GetType("ChiseledWallMounting.RecessedMountRecord")?.MakeByRefType()]);
            if (tryGet == null) return null;
            var args = new object[] { Pos, null };
            if (tryGet.Invoke(null, args) is not true || args[1] == null) return null;
            if (args[1].GetType().GetProperty("Translation")?.GetValue(args[1]) is not Vec3f t || (t.X == 0 && t.Y == 0 && t.Z == 0)) return null;
            capi.Logger.Debug("[Singing Fish] Chiseled Wall Mounting moves the fish at {0} by {1}, {2}, {3}", Pos, t.X, t.Y, t.Z);
            return t;
        }
        catch (Exception e)
        {
            capi.Logger.Warning("[Singing Fish] Could not read Chiseled Wall Mounting's offset at {0}: {1}", Pos, e.Message);
            return null;
        }
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        Release();
    }

    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        Release();
    }

    private void Release()
    {
        if (SingingFishModSystem.ClientStore is { } store) store.Arrived -= OnSongArrived;
        SongLibrary.Ready -= OnSongReady;
        StopSinging(true);
        rig?.Dispose();
        rig = null;
        rigFor = null;
    }

    /// <summary>The fish item's textures (then its model's own), put in the block atlas like the antler mount's still fish.</summary>
    private sealed class FishTextures : ITexPositionSource
    {
        private readonly ICoreClientAPI capi;
        private readonly Item item;
        private readonly Shape shape;
        private readonly Dictionary<string, TextureAtlasPosition> cache = new();

        public FishTextures(ICoreClientAPI capi, Item item, Shape shape) { this.capi = capi; this.item = item; this.shape = shape; }

        public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

        public TextureAtlasPosition this[string code]
        {
            get
            {
                if (cache.TryGetValue(code, out var known)) return known;
                TextureAtlasPosition pos = null;
                CompositeTexture ct = null;
                if (item.Textures != null && !item.Textures.TryGetValue(code, out ct)) item.Textures.TryGetValue("all", out ct);
                if (ct != null)
                {
                    // the game has already baked the item's textures (wildcards picked); bake a copy only if not
                    var baked = ct.Baked;
                    if (baked == null) { ct = ct.Clone(); ct.Bake(capi.Assets); baked = ct.Baked; }
                    capi.BlockTextureAtlas.GetOrInsertTexture(baked.BakedName, out _, out pos);
                }
                else if (shape.Textures != null && shape.Textures.TryGetValue(code, out var loc))
                {
                    capi.BlockTextureAtlas.GetOrInsertTexture(loc.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png"), out _, out pos);
                }
                pos ??= capi.BlockTextureAtlas.UnknownTexturePosition;
                cache[code] = pos;
                return pos;
            }
        }
    }
}
