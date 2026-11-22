namespace SingingFish;

/// <summary>
/// How wide the fish's mouth should be at each moment of a song.
///
/// The song (mono samples, from <see cref="SongLibrary"/>) is measured 30 times a second (the game's animation frame
/// rate) in the voice range, 300 Hz - 3 kHz, with the drums taken out (<see cref="SteadyLevel"/>) - so the bass, the
/// hiss and the drum hits do not move the mouth. That loudness is set against the song's own quiet and loud
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

    /// <summary>Frames where the tail flicks: the song's beats, kept to its tempo (see BeatsFrom). Null when the song
    /// has no clear beat - the tail flicks at set times then.</summary>
    public bool[] TailHits { get; private init; }

    /// <summary>What the tail found in this song (tempo, flicks, how clear the beat is) - written to the client log.</summary>
    public string TailInfo { get; private init; }

    public int Frames => Mouth.Length;

    public static SongTiming FromSamples(float[] mono, int rate)
    {
        int perFrame = Math.Max(1, rate / Fps);
        int frames = Math.Min(mono.Length / perFrame, MaxSeconds * Fps);
        if (frames < Fps) return null;

        // voice-range loudness with the drums taken out
        var level = SteadyLevel(mono, rate, perFrame, frames);

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
        return new SongTiming { Seconds = frames / (float)Fps, Mouth = mouth, HeadOut = HeadFrom(env, floor, span), TailHits = BeatsFrom(mono, rate, frames, out string tailInfo), TailInfo = tailInfo };
    }

    /// <summary>
    /// The voice-range loudness per animation frame with the drums taken out (harmonic-percussive separation by median
    /// filtering, after FitzGerald 2010). The song's spectrum is taken 30 times a second; in it a steady sound (a voice,
    /// a held chord) is a line along time and a drum hit is a line up the frequencies. Each point is compared with the
    /// median of its neighbours along time (HarmonicSpan frames) and up the frequencies (PercussiveSpan bins), and kept
    /// in proportion to how much steadier than hit-like it is. What is left in 300 Hz - 3 kHz is the level the mouth and
    /// head follow, so a drum hit no longer snaps the mouth open. It cannot tell a voice from an instrument holding a
    /// note; it only stops the hits.
    /// </summary>
    private static float[] SteadyLevel(float[] mono, int rate, int perFrame, int frames)
    {
        int size = 256;
        while (size < rate * 0.046) size *= 2;                       // ~46 ms: 1024 at 22 kHz, 2048 at 44 kHz
        double binHz = rate / (double)size;
        int lo = Math.Max(1, (int)(300 / binHz)), hi = Math.Min(size / 2 - 1, (int)(3000 / binHz));
        int pad = PercussiveSpan / 2, first = Math.Max(1, lo - pad), last = Math.Min(size / 2 - 1, hi + pad);
        int bins = last - first + 1;

        // power spectrum per frame, only the bins the voice range (and its frequency neighbours) needs
        var power = new float[frames][];
        var window = new double[size];
        for (int i = 0; i < size; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size);
        var re = new double[size];
        var im = new double[size];
        var (cosT, sinT) = Twiddles(size);
        // two frames per transform: one as the real part, the next as the imaginary part, separated afterwards
        for (int f = 0; f < frames; f += 2)
        {
            int startA = f * perFrame + perFrame / 2 - size / 2, startB = startA + perFrame;
            bool hasB = f + 1 < frames;
            for (int i = 0; i < size; i++)
            {
                int a = startA + i, b = startB + i;
                re[i] = a >= 0 && a < mono.Length ? mono[a] * window[i] : 0;
                im[i] = hasB && b >= 0 && b < mono.Length ? mono[b] * window[i] : 0;
            }
            Fft(re, im, cosT, sinT);
            var rowA = new float[bins];
            var rowB = hasB ? new float[bins] : null;
            for (int c = 0; c < bins; c++)
            {
                int k = first + c, m = size - k;
                // A = (Z[k] + conj Z[N-k]) / 2, B = (Z[k] - conj Z[N-k]) / 2i
                double ar = (re[k] + re[m]) / 2, ai = (im[k] - im[m]) / 2;
                rowA[c] = (float)(ar * ar + ai * ai);
                if (rowB == null) continue;
                double br = (im[k] + im[m]) / 2, bi = (re[m] - re[k]) / 2;
                rowB[c] = (float)(br * br + bi * bi);
            }
            power[f] = rowA;
            if (hasB) power[f + 1] = rowB;
        }

        // steady share of each point: median along time against median up the frequencies (soft mask)
        var level = new float[frames];
        var alongTime = new float[HarmonicSpan];
        var upFreq = new float[PercussiveSpan];
        int half = HarmonicSpan / 2;
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int b = lo - first; b <= hi - first; b++)
            {
                for (int k = 0; k < HarmonicSpan; k++) alongTime[k] = power[Math.Clamp(f - half + k, 0, frames - 1)][b];
                for (int k = 0; k < PercussiveSpan; k++) upFreq[k] = power[f][Math.Clamp(b - pad + k, 0, bins - 1)];
                double h = Median(alongTime), p = Median(upFreq);
                double hh = h * h, pp = p * p;
                if (hh + pp > 0) sum += power[f][b] * hh / (hh + pp);
            }
            level[f] = (float)Math.Sqrt(sum / (hi - lo + 1));
        }
        return level;
    }

    /// <summary>Frames along time (30 a second) and frequency bins the separation compares each point with.</summary>
    private const int HarmonicSpan = 9, PercussiveSpan = 17;

    /// <summary>Median of a short array (sorted in place - insertion sort is fastest for 9 or 17 values).</summary>
    private static float Median(float[] values)
    {
        for (int i = 1; i < values.Length; i++)
        {
            float v = values[i];
            int j = i - 1;
            while (j >= 0 && values[j] > v) { values[j + 1] = values[j]; j--; }
            values[j + 1] = v;
        }
        return values[values.Length / 2];
    }

    private static (double[] cos, double[] sin) Twiddles(int n)
    {
        var c = new double[n / 2];
        var sn = new double[n / 2];
        for (int k = 0; k < n / 2; k++) { c[k] = Math.Cos(-2 * Math.PI * k / n); sn[k] = Math.Sin(-2 * Math.PI * k / n); }
        return (c, sn);
    }

    /// <summary>In-place radix-2 FFT (length a power of two), with its cos/sin table.</summary>
    private static void Fft(double[] re, double[] im, double[] cosT, double[] sinT)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            int halfLen = len / 2, stride = n / len;
            for (int i = 0; i < n; i += len)
                for (int k = 0; k < halfLen; k++)
                {
                    int a = i + k, b = a + halfLen;
                    double cr = cosT[k * stride], ci = sinT[k * stride];
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
        }
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

    /// <summary>
    /// Tail flicks kept in time with the song. The low end (a two-pole low-pass at 150 Hz - kick drum and bass) is
    /// measured 100 times a second and its sudden rises taken as onsets; the song's tempo is the beat spacing whose
    /// onsets line up best (60 - 180 BPM, leaning towards ~120 so it does not pick half or double time), and the beats
    /// are then placed on that tempo where the onsets fall (dynamic programming, after Ellis 2007), so the tail keeps
    /// steady time through stretches where single drum hits do not stand out, but stays still on beats with no hit
    /// (a soft intro, a quiet bridge). One flick per beat, every second beat above 140 BPM. Null when the song has no clear beat (the tail flicks at set times then).
    /// </summary>
    private static bool[] BeatsFrom(float[] mono, int rate, int frames, out string info)
    {
        const int Hz = 100;
        int hop = Math.Max(1, rate / Hz);
        int n = Math.Min(mono.Length / hop, (int)((long)frames * Hz / Fps));
        info = "too short";
        if (n < Hz * 4) return null;

        // low end: Butterworth low-pass at 150 Hz (biquad)
        double w0 = 2 * Math.PI * 150.0 / rate, cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * 0.7071);
        double a0 = 1 + alpha, b0 = (1 - cos) / 2 / a0, b1 = (1 - cos) / a0, b2 = b0, a1 = -2 * cos / a0, a2 = (1 - alpha) / a0;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        var energy = new double[n];
        for (int f = 0; f < n; f++)
        {
            double sum = 0;
            for (int i = f * hop, end = i + hop; i < end; i++)
            {
                double x = mono[i], y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
                sum += y * y;
            }
            energy[f] = sum / hop;
        }

        // onsets: rises in loudness (log energy, quiet parts floored so silence does not count)
        var sorted = (double[])energy.Clone();
        Array.Sort(sorted);
        double floor = Math.Max(1e-10, sorted[(int)(n * 0.3)]);
        var onset = new double[n];
        double prev = Math.Log(Math.Max(energy[0], floor));
        for (int f = 1; f < n; f++)
        {
            double l = Math.Log(Math.Max(energy[f], floor));
            onset[f] = Math.Max(0, l - prev);
            prev = l;
        }
        double mean = onset.Average(), sd = Math.Sqrt(onset.Sum(v => (v - mean) * (v - mean)) / n);
        info = "no clear beat";
        if (sd < 1e-9) return null;
        for (int f = 0; f < n; f++) onset[f] /= sd;
        double nmean = mean / sd;

        // tempo: the beat spacing whose onsets line up best, weighted towards ~120 BPM
        int period = 0;
        double bestTempo = double.MinValue;
        for (int lag = Hz * 60 / 180; lag <= Hz * 60 / 60; lag++)
        {
            double ac = 0;
            for (int f = lag; f < n; f++) ac += (onset[f] - nmean) * (onset[f - lag] - nmean);
            ac /= n - lag;
            double bpmAt = 60.0 * Hz / lag, octaves = Math.Log2(bpmAt / 120.0);
            double score = ac * Math.Exp(-0.5 * octaves * octaves / (0.9 * 0.9));
            if (score > bestTempo) { bestTempo = score; period = lag; }
        }
        if (period == 0 || bestTempo <= 0) return null;

        // beats on that tempo where the onsets fall
        const double Tight = 100;
        var score2 = new double[n];
        var from = new int[n];
        for (int t = 0; t < n; t++)
        {
            double best = 0;
            int bestFrom = -1;
            for (int p = Math.Max(0, t - 2 * period), hi = t - period / 2; p <= hi; p++)
            {
                double d = Math.Log((t - p) / (double)period);
                double v = score2[p] - Tight * d * d;
                if (bestFrom < 0 || v > best) { best = v; bestFrom = p; }
            }
            score2[t] = onset[t] + (bestFrom >= 0 ? Math.Max(0, best) : 0);
            from[t] = bestFrom >= 0 && best > 0 ? bestFrom : -1;
        }
        int last = n - 1;
        for (int t = Math.Max(0, n - period); t < n; t++) if (score2[t] > score2[last]) last = t;
        var beats = new List<int>();
        for (int t = last; t >= 0; t = from[t]) beats.Add(t);
        beats.Reverse();

        // how strongly the beats sit on onsets compared with any moment of the song
        double onBeat = 0;
        foreach (int b in beats)
        {
            double m = 0;
            for (int k = Math.Max(0, b - 2); k <= Math.Min(n - 1, b + 2); k++) m = Math.Max(m, onset[k]);
            onBeat += m;
        }
        onBeat /= Math.Max(1, beats.Count);
        double offBeat = 0;
        for (int f = 0; f < n; f++)
        {
            double m = 0;
            for (int k = Math.Max(0, f - 2); k <= Math.Min(n - 1, f + 2); k++) m = Math.Max(m, onset[k]);
            offBeat += m;
        }
        offBeat /= n;
        double strength = onBeat / Math.Max(1e-9, offBeat);
        double bpm = 60.0 * Hz / period;
        if (beats.Count < 8 || strength < MinBeatStrength)
        {
            info = $"no clear beat (tempo {bpm:0} BPM, strength {strength:0.00}) - flicks at set times";
            return null;
        }

        // only beats with a real hit on them: the tempo runs through a soft intro, a quiet bridge or an outro too, and
        // the tail should keep still there. A beat counts when an onset near it is at least ActiveShare of a typical
        // beat's; a single quiet beat between two that count is kept, so a busy part does not stutter.
        var peak = new double[beats.Count];
        for (int i = 0; i < beats.Count; i++)
            for (int k = Math.Max(0, beats[i] - 3); k <= Math.Min(n - 1, beats[i] + 3); k++) peak[i] = Math.Max(peak[i], onset[k]);
        var byPeak = (double[])peak.Clone();
        Array.Sort(byPeak);
        double typical = byPeak[byPeak.Length / 2];
        var active = new bool[beats.Count];
        for (int i = 0; i < beats.Count; i++) active[i] = peak[i] >= ActiveShare * typical;
        for (int i = 1; i + 1 < beats.Count; i++)
            if (!active[i] && peak[i - 1] >= ActiveShare * typical && peak[i + 1] >= ActiveShare * typical) active[i] = true;

        var hits = new bool[frames];
        int step = bpm > 140 ? 2 : 1, flicks = 0, still = 0;
        for (int i = 0; i < beats.Count; i += step)
        {
            if (!active[i]) { still++; continue; }
            int frame = (int)Math.Round(beats[i] * (double)Fps / Hz);
            if (frame < frames) { hits[frame] = true; flicks++; }
        }
        info = $"tempo {bpm:0} BPM, {flicks} flicks{(step == 2 ? " (every second beat)" : "")}, {still} quiet beats left out, strength {strength:0.00}";
        return hits;
    }

    /// <summary>A beat flicks the tail when the hit near it is at least this share of a typical beat's hit.</summary>
    private const double ActiveShare = 0.35;

    /// <summary>How much more onset the beats must catch than the song's average moment to count as a clear beat.</summary>
    private const double MinBeatStrength = 1.3;
}
