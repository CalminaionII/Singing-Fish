using OpenTK.Audio.OpenAL;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace SingingFish;

/// <summary>One song, ready to sing: its sound (mono 16-bit) and its mouth timing.</summary>
public sealed class SongData
{
    public short[] Pcm { get; init; }
    public int Rate { get; init; }
    public float Seconds => Pcm.Length / (float)Rate;
    public SongTiming Timing { get; init; }
}

/// <summary>
/// CLIENT: songs decoded once, in the background - so nothing heavy happens on the game's main thread. An Ogg (the mod's own song,
/// own folder, download) is read with NVorbis, mixed to mono and kept as 16-bit samples, and its mouth timing worked out
/// from the same samples. A few songs are kept; the oldest unused goes first.
/// </summary>
public static class SongLibrary
{
    private const int MaxSeconds = 6 * 60;
    private const int Keep = 8;

    private static readonly Dictionary<string, SongData> ready = new();
    private static readonly List<string> order = new();
    private static readonly HashSet<string> working = new();
    private static readonly object gate = new();

    /// <summary>A song finished decoding (raised on the main thread).</summary>
    public static event Action<string> Ready;

    public static void Reset() { lock (gate) { ready.Clear(); order.Clear(); working.Clear(); } Ready = null; }

    /// <summary>The song if decoded; otherwise null, and decoding is started (bytes are fetched now, on this thread).</summary>
    public static SongData Get(ICoreClientAPI capi, string key, Func<byte[]> bytes)
    {
        if (key == null) return null;
        lock (gate)
        {
            if (ready.TryGetValue(key, out var d)) { order.Remove(key); order.Add(key); return d; }
            if (!working.Add(key)) return null;
        }
        byte[] data;
        try { data = bytes?.Invoke(); } catch (Exception) { data = null; }
        if (data == null) { lock (gate) working.Remove(key); return null; }
        Task.Run(() =>
        {
            SongData song = null;
            try { song = Decode(data); }
            catch (Exception e) { capi.Logger.Warning("[Singing Fish] Could not read the song {0}: {1}", key, e.Message); }
            lock (gate)
            {
                working.Remove(key);
                if (song == null) return;
                ready[key] = song;
                order.Remove(key); order.Add(key);
                while (order.Count > Keep) { ready.Remove(order[0]); order.RemoveAt(0); }
            }
            capi.Event.EnqueueMainThreadTask(() =>
            {
                // what the tail found, so a song whose tail does not keep time can be looked into
                capi.Logger.Notification("[Singing Fish] Tail for {0}: {1}", NameOf(key), song.Timing?.TailInfo ?? "song too short");
                Ready?.Invoke(key);
            }, "singingfish-song-ready");
        });
        return null;
    }

    /// <summary>A song's name for the log: its file for the mod's own, else the name the server or songs folder gives it.</summary>
    private static string NameOf(string key)
    {
        if (key.StartsWith("pack:")) return Path.GetFileNameWithoutExtension(key[5..]);
        if (!key.StartsWith("user:")) return key;
        string id = key[5..];
        return SingingFishModSystem.Catalog.FirstOrDefault(s => s.Id == id)?.Name
            ?? SingingFishModSystem.ClientStore?.OwnSongs.FirstOrDefault(s => s.Id == id)?.Name
            ?? key;
    }

    private static SongData Decode(byte[] ogg)
    {
        using var stream = new MemoryStream(ogg);
        using var vorbis = new NVorbis.VorbisReader(stream, false);
        int ch = Math.Max(1, vorbis.Channels), rate = vorbis.SampleRate;
        int max = MaxSeconds * rate;
        var mono = new List<float>(rate * 120);
        var buf = new float[4096 * ch];
        int n;
        while ((n = vorbis.ReadSamples(buf, 0, buf.Length)) > 0 && mono.Count < max)
            for (int i = 0; i + ch <= n; i += ch)
            {
                float s = 0;
                for (int c = 0; c < ch; c++) s += buf[i + c];
                mono.Add(s / ch);
            }
        if (mono.Count == 0) return null;
        var samples = mono.ToArray();
        var pcm = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++) pcm[i] = (short)Math.Clamp(samples[i] * 32767f, -32768f, 32767f);
        return new SongData { Pcm = pcm, Rate = rate, Timing = SongTiming.FromSamples(samples, rate) };
    }
}

/// <summary>
/// CLIENT: one song playing from a mounted fish, straight through OpenAL (the library the game itself plays sound with,
/// shipped in its Lib folder). The game's own sound system cannot play a song that was not installed when it started -
/// seen in game: a sound added later is "Audio File not found", and a placeholder whose data is swapped still plays the
/// placeholder - so players' songs are played here instead.
///
/// Placed in the world without knowing how the game sets up its listener: every tick the source is put at the
/// listener's own position plus (fish - camera), which is right whether the game keeps its listener at the camera's
/// world position or at the origin. Volume follows the game's sound volume setting; it fades over 32 blocks like the
/// game's own sounds, and keeps playing while the game is paused. Any OpenAL failure is logged once and the fish just does not sing.
/// </summary>
public sealed class SongPlayer : IDisposable
{
    private readonly ICoreClientAPI capi;
    private readonly Vec3d at;
    private int source, buffer;
    private bool started;

    private SongPlayer(ICoreClientAPI capi, Vec3d at) { this.capi = capi; this.at = at; }

    private static bool warned;

    public static SongPlayer Play(ICoreClientAPI capi, SongData song, Vec3d at, float volume)
    {
        var p = new SongPlayer(capi, at);
        try
        {
            p.buffer = AL.GenBuffer();
            AL.BufferData(p.buffer, ALFormat.Mono16, song.Pcm, song.Rate);
            p.source = AL.GenSource();
            AL.Source(p.source, ALSourcei.Buffer, p.buffer);
            AL.Source(p.source, ALSourceb.Looping, false);
            AL.Source(p.source, ALSourcef.ReferenceDistance, 3f);
            AL.Source(p.source, ALSourcef.MaxDistance, 32f);
            AL.Source(p.source, ALSourcef.RolloffFactor, 1f);
            p.Update(volume);
            AL.SourcePlay(p.source);
            var err = AL.GetError();
            if (err != ALError.NoError) throw new InvalidOperationException("OpenAL said " + err);
            p.started = true;
            return p;
        }
        catch (Exception e)
        {
            if (!warned) capi.Logger.Warning("[Singing Fish] Could not play a song through OpenAL: {0}", e.Message);
            warned = true;
            p.Dispose();
            return null;
        }
    }

    /// <summary>Position and volume (the fish's own 0..1 times the game's sound volume) - call every tick while it plays.</summary>
    public void Update(float fishVolume)
    {
        if (source == 0) return;
        try
        {
            AL.GetListener(ALListener3f.Position, out float lx, out float ly, out float lz);
            var cam = capi.World.Player?.Entity?.CameraPos;
            if (cam != null)
                AL.Source(source, ALSource3f.Position, lx + (float)(at.X - cam.X), ly + (float)(at.Y - cam.Y), lz + (float)(at.Z - cam.Z));
            float volume = Math.Clamp(capi.Settings.Int["soundLevel"] / 100f, 0f, 1f) * Math.Clamp(fishVolume, 0f, 1f);
            AL.Source(source, ALSourcef.Gain, volume);
        }
        catch (Exception) { }
    }

    public bool Playing
    {
        get
        {
            if (source == 0 || !started) return false;
            try { AL.GetSource(source, ALGetSourcei.SourceState, out int state); return state == (int)ALSourceState.Playing || state == (int)ALSourceState.Paused; }
            catch (Exception) { return false; }
        }
    }

    public void Dispose()
    {
        try
        {
            if (source != 0) { AL.SourceStop(source); AL.DeleteSource(source); }
            if (buffer != 0) AL.DeleteBuffer(buffer);
        }
        catch (Exception) { }
        source = buffer = 0;
    }
}
