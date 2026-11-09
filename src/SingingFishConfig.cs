using Vintagestory.API.Common;

namespace SingingFish;

/// <summary>
/// SingingFishConfig.json in ModConfig. SERVER settings: the server is the one taking uploads, so its file is the one
/// that counts. Read at start and again whenever a config manager (Integrated Mod Manager, ConfigLib, ConfigKit) saves
/// it; out-of-range values are clamped and the corrected file written back.
/// </summary>
public sealed class SingingFishConfig
{
    public const string FileName = "SingingFishConfig.json";

    /// <summary>Whether players may put their own songs on this server. Off: only song packs.</summary>
    public bool AllowPlayerSongs { get; set; } = true;

    /// <summary>The largest song a player may upload, in megabytes (1 - 20).</summary>
    public int MaxSongMB { get; set; } = 6;

    /// <summary>How many of their own songs each player may keep on the server (0 - 100).</summary>
    public int MaxSongsPerPlayer { get; set; } = 10;

    public int MaxSongBytes => MaxSongMB * 1024 * 1024;

    /// <summary>The last load had to correct a value (so a config manager should re-read the file).</summary>
    [Newtonsoft.Json.JsonIgnore] public bool WasClamped { get; private set; }

    public static SingingFishConfig Load(ICoreAPI api)
    {
        SingingFishConfig c;
        try { c = api.LoadModConfig<SingingFishConfig>(FileName); }
        catch (Exception e)
        {
            api.Logger.Error("[Singing Fish] {0} could not be read - the defaults stand: {1}", FileName, e.Message);
            return new SingingFishConfig();
        }
        if (c == null)
        {
            c = new SingingFishConfig();
            try { api.StoreModConfig(c, FileName); } catch (Exception) { }
        }
        int mb = Math.Clamp(c.MaxSongMB, 1, 20), count = Math.Clamp(c.MaxSongsPerPlayer, 0, 100);
        if (mb != c.MaxSongMB || count != c.MaxSongsPerPlayer)
        {
            c.MaxSongMB = mb;
            c.MaxSongsPerPlayer = count;
            c.WasClamped = true;
            try { api.StoreModConfig(c, FileName); } catch (Exception) { }
        }
        return c;
    }
}
