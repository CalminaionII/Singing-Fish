namespace SingingFish;

/// <summary>
/// How wide the fish's mouth should be at each moment of a song.
///
/// The song (mono samples, from <see cref="SongLibrary"/>) is filtered to the voice range - a one-pole high-pass near
/// 300 Hz and low-pass near 3 kHz, which keeps most singing and drops the bass and hiss - and its loudness measured 30
/// times a second (the game's animation frame rate). That loudness is set against the song's own quiet and loud
/// levels, and the mouth opens on the RISES (a syllable starting) as well as with the level, so it snaps open and shut
/// like the toy rather than hanging open. A long loud stretch gets a gentle chatter so it never freezes wide open. It
/// cannot pick words out of music; it follows the voice range closely enough to look like singing.
/// </summary>
public sealed class SongTiming
{
    public const int Fps = 30;
    private const int MaxSeconds = 6 * 60;   // the longest a song plays (SongLibrary keeps six minutes)

    /// <summary>Length timed, in seconds (at most six minutes, as long as a song plays).</summary>
    public float Seconds { get; private init; }

    /// <summary>Mouth openness 0..1 per animation frame.</summary>
    public float[] Mouth { get; private init; }

    /// <summary>
    /// Head (and front body) out per frame: out while the voice range has been busy for a moment, back in the gaps
    /// between lines and verses, each held at least a second or so. Null when the song gives no usable answer (singing
    /// all the way through or never) - the fish turns at random then.
    /// </summary>
    public bool[] HeadOut { get; private init; }

    /// <summary>Frames where the tail flicks: strong beats in the low end (drums, bass), at least 0.6 s apart. Null when
    /// the song has too few to use - the tail flicks at random then.</summary>
    public bool[] TailHits { get; private init; }

    public int Frames => Mouth.Length;

    public static SongTiming FromSamples(float[] mono, int rate)
    {
        int perFrame = Math.Max(1, rate / Fps);
        int frames = Math.Min(mono.Length / perFrame, MaxSeconds * Fps);
        if (frames < Fps) return null;

        // voice-range filters (one-pole): high-pass ~300 Hz, low-pass ~3 kHz
        double hpA = Math.Exp(-2 * Math.PI * 300.0 / rate), lpA = Math.Exp(-2 * Math.PI * 3000.0 / rate);
        double hpPrevIn = 0, hpPrevOut = 0, lp = 0;
        var level = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * perFrame, end = i + perFrame; i < end; i++)
            {
                double x = mono[i];
                double hp = hpA * (hpPrevOut + x - hpPrevIn);
                hpPrevIn = x; hpPrevOut = hp;
                lp = lpA * lp + (1 - lpA) * hp;
                sum += lp * lp;
            }
            level[f] = (float)Math.Sqrt(sum / perFrame);
        }

        // smooth: quick attack, slower release (so a syllable reads as one opening)
        var env = new float[frames];
        float e = 0;
        for (int i = 0; i < frames; i++)
        {
            float v = level[i];
            e = v > e ? e + (v - e) * 0.7f : e + (v - e) * 0.35f;
            env[i] = e;
        }

        // against the song's own quiet and loud levels
        var sorted = (float[])env.Clone();
        Array.Sort(sorted);
        float floor = sorted[(int)(sorted.Length * 0.30)], peak = sorted[(int)(sorted.Length * 0.97)];
        float span = Math.Max(1e-6f, peak - floor);

        var mouth = new float[frames];
        int held = 0;
        for (int i = 0; i < frames; i++)
        {
            float norm = Math.Clamp((env[i] - floor) / span, 0, 1);
            float rise = i >= 3 ? Math.Max(0, norm - Math.Clamp((env[i - 3] - floor) / span, 0, 1)) : 0;
            float open = Math.Clamp(0.55f * norm + 1.6f * rise, 0, 1);
            if (open < 0.12f) open = 0;
            held = open > 0.5f ? held + 1 : 0;
            if (held > 8) open *= 0.55f + 0.45f * MathF.Abs(MathF.Sin(MathF.PI * i * 4f / Fps));   // chatter on a long note
            mouth[i] = open;
        }
        return new SongTiming { Seconds = frames / (float)Fps, Mouth = mouth, HeadOut = HeadFrom(env, floor, span), TailHits = BeatsFrom(mono, rate, perFrame, frames) };
    }

    /// <summary>Out while the singing goes on (voice level over ~0.6 s), with hysteresis and minimum holds.</summary>
    private static bool[] HeadFrom(float[] env, float floor, float span)
    {
        int n = env.Length, win = Fps * 6 / 10;
        var avg = new float[n];
        double run = 0;
        for (int i = 0; i < n; i++)
        {
            run += Math.Clamp((env[i] - floor) / span, 0, 1);
            if (i >= win) run -= Math.Clamp((env[i - win] - floor) / span, 0, 1);
            avg[i] = (float)(run / Math.Min(i + 1, win));
        }
        var outAt = new bool[n];
        bool isOut = false;
        int since = 0, quiet = 0, outFrames = 0;
        int minOut = Fps * 12 / 10, minIn = Fps * 6 / 10, needQuiet = Fps * 8 / 10;
        for (int i = 0; i < n; i++)
        {
            since++;
            quiet = avg[i] < 0.18f ? quiet + 1 : 0;
            if (!isOut && avg[i] > 0.35f && since >= minIn) { isOut = true; since = 0; }
            else if (isOut && quiet >= needQuiet && since >= minOut) { isOut = false; since = 0; }
            outAt[i] = isOut;
            if (isOut) outFrames++;
        }
        float share = outFrames / (float)Math.Max(1, n);
        return share is < 0.1f or > 0.92f ? null : outAt;   // no real gaps, or hardly any singing: random instead
    }

    /// <summary>Strong low-end beats: energy below ~150 Hz jumping to twice its recent level, at least 0.6 s apart.</summary>
    private static bool[] BeatsFrom(float[] mono, int rate, int perFrame, int frames)
    {
        double a = Math.Exp(-2 * Math.PI * 150.0 / rate), low = 0;
        var energy = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * perFrame, end = i + perFrame; i < end; i++)
            {
                low = a * low + (1 - a) * mono[i];
                sum += low * low;
            }
            energy[f] = (float)(sum / perFrame);
        }
        var sorted = (float[])energy.Clone();
        Array.Sort(sorted);
        float floor = sorted[(int)(sorted.Length * 0.4)];
        var hits = new bool[frames];
        int last = -1000, count = 0, minGap = Fps * 6 / 10, look = Fps / 2;
        for (int f = look; f < frames; f++)
        {
            double mean = 0;
            for (int k = f - look; k < f; k++) mean += energy[k];
            mean /= look;
            if (energy[f] > floor && energy[f] > 2.0 * mean && f - last >= minGap) { hits[f] = true; last = f; count++; }
        }
        return count / (frames / (float)Fps) < 0.1f ? null : hits;     // fewer than one every 10 s: random instead
    }
}
