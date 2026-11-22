using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SingingFish;

/// <summary>
/// SingingFishConfig.json in ModConfig. SERVER settings: the server is the one taking uploads, so its file is the one
/// that counts. Read at start and again whenever a config manager (Integrated Mod Manager, ConfigLib, ConfigKit) saves
/// it; out-of-range values are corrected and the corrected file written back.
/// </summary>
public sealed class SingingFishConfig
{
    public const string FileName = "SingingFishConfig.json";
    public const string Everyone = "Everyone", Admins = "Admins", Nobody = "Nobody";

    /// <summary>Who may add their own songs to this server: "Everyone", "Admins" (players with controlserver) or
    /// "Nobody". Nobody also hides songs players added before from the song menu and Random. Null in a file from before
    /// 1.1.0 - then taken from <see cref="AllowPlayerSongs"/>.</summary>
    public string PlayerSongs { get; set; }

    /// <summary>OLD (1.0.0 - 1.0.1) on/off for players' songs: read from an old file to set <see cref="PlayerSongs"/>,
    /// never written again.</summary>
    public bool? AllowPlayerSongs { get; set; }
    public bool ShouldSerializeAllowPlayerSongs() => false;

    /// <summary>Whether players see each other's songs in the song menu. Off: each player sees only their own and the
    /// server's (admins still see all, so they can remove any). Songs still play for everyone nearby, and Random is
    /// <see cref="RandomPlaysPlayerSongs"/>'s business.</summary>
    public bool ShowEveryonesSongs { get; set; } = true;

    /// <summary>Whether Random may pick songs players added (server songs and the mod's own songs it always may).</summary>
    public bool RandomPlaysPlayerSongs { get; set; } = true;

    /// <summary>Whether the two songs that come with the mod are offered (song menu and Random).</summary>
    public bool IncludeBuiltInSongs { get; set; } = true;

    /// <summary>The largest song a player may upload, in megabytes (1 - 20).</summary>
    public int MaxSongMB { get; set; } = 6;

    /// <summary>How many of their own songs each player may keep on the server (0 - 100).</summary>
    public int MaxSongsPerPlayer { get; set; } = 10;

    public int MaxSongBytes => MaxSongMB * 1024 * 1024;

    /// <summary>Players' songs are listed and played here at all (not "Nobody").</summary>
    [Newtonsoft.Json.JsonIgnore] public bool PlayerSongsOn => PlayerSongs != Nobody;

    public static bool IsAdmin(IServerPlayer player) => player?.HasPrivilege(Privilege.controlserver) == true;

    /// <summary>Whether this player may add a song of their own.</summary>
    public bool CanUpload(IServerPlayer player) => PlayerSongs == Everyone || PlayerSongs == Admins && IsAdmin(player);

    /// <summary>The last load had to correct a value (so a config manager should re-read the file).</summary>
    [Newtonsoft.Json.JsonIgnore] public bool WasClamped { get; private set; }

    public static SingingFishConfig Load(ICoreAPI api)
    {
        SingingFishConfig c;
        try { c = api.LoadModConfig<SingingFishConfig>(FileName); }
        catch (Exception e)
        {
            api.Logger.Error("[Singing Fish] {0} could not be read - the defaults stand: {1}", FileName, e.Message);
            c = new SingingFishConfig();
            c.Correct();
            return c;
        }
        bool fresh = c == null;
        c ??= new SingingFishConfig();
        // a setting added in a later version is missing from an older file: write it in, so the config managers (which
        // read the file) show its real value instead of an empty box
        bool missing = !fresh && MissingSetting(api);
        if (c.Correct() | fresh | missing)
        {
            c.WasClamped = !fresh;
            try { api.StoreModConfig(c, FileName); } catch (Exception) { }
        }
        return c;
    }

    private static readonly string[] Settings =
    [
        nameof(PlayerSongs), nameof(ShowEveryonesSongs), nameof(RandomPlaysPlayerSongs), nameof(IncludeBuiltInSongs),
        nameof(MaxSongMB), nameof(MaxSongsPerPlayer)
    ];

    /// <summary>Whether the file on disk lacks any of the settings (an older version wrote it).</summary>
    private static bool MissingSetting(ICoreAPI api)
    {
        try
        {
            var raw = api.LoadModConfig(FileName);
            return raw != null && Settings.Any(name => !raw[name].Exists);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Puts every value in range (and an old file's AllowPlayerSongs into PlayerSongs); true if anything changed.</summary>
    private bool Correct()
    {
        bool changed = false;
        string mode = PlayerSongs?.Trim() switch
        {
            { } s when s.Equals(Everyone, StringComparison.OrdinalIgnoreCase) => Everyone,
            { } s when s.StartsWith("Admin", StringComparison.OrdinalIgnoreCase) => Admins,
            { } s when s.Equals(Nobody, StringComparison.OrdinalIgnoreCase) => Nobody,
            _ => AllowPlayerSongs == false ? Nobody : Everyone
        };
        if (mode != PlayerSongs || AllowPlayerSongs != null) changed = true;
        PlayerSongs = mode;
        AllowPlayerSongs = null;
        int mb = Math.Clamp(MaxSongMB, 1, 20), count = Math.Clamp(MaxSongsPerPlayer, 0, 100);
        if (mb != MaxSongMB || count != MaxSongsPerPlayer) changed = true;
        MaxSongMB = mb;
        MaxSongsPerPlayer = count;
        return changed;
    }
}
