using OggVorbisEncoder;

namespace SingingFish;

/// <summary>
/// Turns a player's MP3 or WAV into the Ogg Vorbis the game can play: decoded (MP3 by NLayer, WAV read here), mixed down
/// to MONO - the game only places mono sounds in the world, so a stereo song would be heard everywhere at full volume -
/// resampled to 22 050 Hz and encoded by OggVorbisEncoder at a modest quality, so a three-minute song is around a
/// megabyte. Runs off the main thread; ClientSongStore caches the result.
/// </summary>
public static class SongConverter
{
    public const int OutRate = 22050;
    private const float Quality = 0.4f;
    private const int MaxSeconds = 8 * 60;

    public static bool CanConvert(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        return ext is ".mp3" or ".wav";
    }

    public static byte[] ToMonoOgg(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        var (mono, rate) = ext == ".mp3" ? DecodeMp3(file) : DecodeWav(File.ReadAllBytes(file));
        if (mono.Length == 0 || rate <= 0) throw new InvalidDataException("no audio in the file");
        return Encode(Resample(mono, rate, OutRate), OutRate);
    }

    // ------------------------------------------------------------------ decoding

    private static (float[] mono, int rate) DecodeMp3(string file)
    {
        using var fs = File.OpenRead(file);
        var mp3 = new NLayer.MpegFile(fs);
        int ch = Math.Max(1, mp3.Channels), rate = mp3.SampleRate;
        int max = MaxSeconds * rate;
        var outList = new List<float>(rate * 60);
        var buf = new float[4096 * ch];
        int n;
        while ((n = mp3.ReadSamples(buf, 0, buf.Length)) > 0 && outList.Count < max)
            for (int i = 0; i + ch <= n; i += ch)
            {
                float s = 0;
                for (int c = 0; c < ch; c++) s += buf[i + c];
                outList.Add(s / ch);
            }
        return (outList.ToArray(), rate);
    }

    /// <summary>RIFF/WAVE: PCM 8/16/24/32-bit or 32-bit float, any channel count (also WAVE_FORMAT_EXTENSIBLE).</summary>
    private static (float[] mono, int rate) DecodeWav(byte[] b)
    {
        if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
            throw new InvalidDataException("not a WAV file");
        int format = 0, channels = 0, rate = 0, bits = 0, dataAt = -1, dataLen = 0;
        int p = 12;
        while (p + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, p, 4);
            int len = BitConverter.ToInt32(b, p + 4);
            int body = p + 8;
            if (len < 0 || body + len > b.Length) len = b.Length - body;
            if (id == "fmt ")
            {
                format = BitConverter.ToUInt16(b, body);
                channels = BitConverter.ToUInt16(b, body + 2);
                rate = BitConverter.ToInt32(b, body + 4);
                bits = BitConverter.ToUInt16(b, body + 14);
                if (format == 0xFFFE && len >= 26) format = BitConverter.ToUInt16(b, body + 24);   // extensible: sub-format
            }
            else if (id == "data") { dataAt = body; dataLen = len; break; }
            p = body + len + (len & 1);
        }
        if (dataAt < 0 || channels <= 0 || rate <= 0 || (format != 1 && format != 3)) throw new InvalidDataException("unsupported WAV");
        int bytesPer = bits / 8;
        if (bytesPer is < 1 or > 4 || (format == 3 && bits != 32)) throw new InvalidDataException("unsupported WAV sample size");
        int frames = Math.Min(dataLen / (bytesPer * channels), MaxSeconds * rate);
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float s = 0;
            for (int c = 0; c < channels; c++)
            {
                int at = dataAt + (f * channels + c) * bytesPer;
                s += format == 3 ? BitConverter.ToSingle(b, at) : bytesPer switch
                {
                    1 => (b[at] - 128) / 128f,
                    2 => BitConverter.ToInt16(b, at) / 32768f,
                    3 => ((b[at] | (b[at + 1] << 8) | (b[at + 2] << 16)) << 8 >> 8) / 8388608f,
                    _ => BitConverter.ToInt32(b, at) / 2147483648f
                };
            }
            mono[f] = s / channels;
        }
        return (mono, rate);
    }

    private static float[] Resample(float[] input, int from, int to)
    {
        if (from == to) return input;
        int n = (int)((long)input.Length * to / from);
        var output = new float[n];
        double step = (double)from / to;
        for (int i = 0; i < n; i++)
        {
            double x = i * step;
            int i0 = (int)x;
            int i1 = Math.Min(i0 + 1, input.Length - 1);
            float t = (float)(x - i0);
            output[i] = input[i0] * (1 - t) + input[i1] * t;
        }
        return output;
    }

    // ------------------------------------------------------------------ encoding (after OggVorbisEncoder's example)

    private static byte[] Encode(float[] mono, int rate)
    {
        for (int i = 0; i < mono.Length; i++) mono[i] = Math.Clamp(mono[i], -1f, 1f);
        var channels = new[] { mono };
        using var output = new MemoryStream();
        var info = VorbisInfo.InitVariableBitRate(1, rate, Quality);
        var ogg = new OggStream(new Random().Next());
        ogg.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        ogg.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(new Comments()));
        ogg.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
        Flush(ogg, output, true);

        var state = ProcessingState.Create(info);
        const int step = 1024;
        for (int at = 0; at < mono.Length; at += step)
        {
            state.WriteData(channels, Math.Min(step, mono.Length - at), at);
            while (!ogg.Finished && state.PacketOut(out OggPacket packet)) { ogg.PacketIn(packet); Flush(ogg, output, false); }
        }
        state.WriteEndOfStream();
        while (!ogg.Finished && state.PacketOut(out OggPacket packet)) { ogg.PacketIn(packet); Flush(ogg, output, false); }
        Flush(ogg, output, true);
        return output.ToArray();
    }

    private static void Flush(OggStream ogg, Stream output, bool force)
    {
        while (ogg.PageOut(out OggPage page, force))
        {
            output.Write(page.Header, 0, page.Header.Length);
            output.Write(page.Body, 0, page.Body.Length);
        }
    }
}
