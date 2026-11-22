using AudioVibes.Decoder;
using NVorbis;

namespace AudioVibes.Decoder.Tests;

internal static class TestSignals
{
    public static readonly ProtocolSpec P = ProtocolSpec.LoadDefault();
    private static readonly Dictionary<int, float[]> Cache = new();

    public static string SoundsDir => Path.Combine(AppContext.BaseDirectory, "sounds");

    /// <summary>The generated cmd_XX.ogg, decoded to mono float at protocol rate.</summary>
    public static float[] Chirp(int code) => Load(code, $"cmd_{code:00}.ogg");

    /// <summary>Generated zone_P_XX.ogg (part 0 = high byte, part 1 = low byte).</summary>
    public static float[] ZoneChirp(int part, int b) => Load(1000 + part * 256 + b, $"zone_{part}_{b:x2}.ogg");

    /// <summary>Both zone frames for a 16-bit zone id, 250 ms apart (the addon's send spacing).</summary>
    public static void MixZone(float[] dst, int zoneId, int at, float gain = 1f)
    {
        MixInto(dst, ZoneChirp(0, zoneId >> 8), at, gain);
        MixInto(dst, ZoneChirp(1, zoneId & 0xFF), at + P.SampleRate / 4, gain);
    }

    private static float[] Load(int cacheKey, string file)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(cacheKey, out var c)) return c;
            using var v = new VorbisReader(Path.Combine(SoundsDir, file));
            if (v.SampleRate != P.SampleRate) throw new InvalidDataException($"ogg rate {v.SampleRate}");
            var all = new List<float>();
            var buf = new float[4096 * v.Channels];
            int n;
            while ((n = v.ReadSamples(buf, 0, buf.Length)) > 0)
                for (int i = 0; i < n; i += v.Channels)
                {
                    float acc = 0;
                    for (int ch = 0; ch < v.Channels; ch++) acc += buf[i + ch];
                    all.Add(acc / v.Channels);
                }
            return Cache[cacheKey] = all.ToArray();
        }
    }

    public static float Gain(double fromDb, double toDb) => (float)Math.Pow(10, (toDb - fromDb) / 20);

    public static void MixInto(float[] dst, float[] src, int at, float gain)
    {
        for (int i = 0; i < src.Length && at + i < dst.Length; i++) dst[at + i] += src[i] * gain;
    }

    /// <summary>Synthetic "gameplay": pink noise body, an HF hiss floor, and random broadband bursts
    /// (spell impacts). Stand-in until real fixtures are recorded with the debug window.</summary>
    public static float[] SyntheticGameplay(int samples, int seed, double pinkRmsDb = -25, double hissRmsDb = -70, bool bursts = true)
    {
        var rng = new Random(seed);
        var x = new float[samples];
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        double pinkGain = Math.Pow(10, pinkRmsDb / 20) / 0.11 * 0.5; // Kellet filter ≈ unity-ish scale
        double hiss = Math.Pow(10, hissRmsDb / 20);
        int burstLeft = 0; double burstAmp = 0;
        double lp1 = 0, lp2 = 0, lpA = 1 - Math.Exp(-2 * Math.PI * 6000.0 / 48000);
        for (int i = 0; i < samples; i++)
        {
            double w = Gauss(rng);
            b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759;
            b2 = 0.96900 * b2 + w * 0.1538520; b3 = 0.86650 * b3 + w * 0.3104856;
            b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
            double pink = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362) * 0.11;
            b6 = w * 0.115926;
            double v = pink * pinkGain;
            if (bursts && burstLeft == 0 && rng.NextDouble() < 1.0 / (48000 * 3)) // ~every 3 s
            {
                burstLeft = rng.Next(2400, 24000);
                burstAmp = Math.Pow(10, rng.Next(-45, -20) / 20.0);
            }
            if (burstLeft > 0) { v += Gauss(rng) * burstAmp; burstLeft--; }
            // Game mixes roll off well above 10 kHz: two one-pole low-passes at ~6 kHz (≈ −19 dB at 17 kHz).
            lp1 += lpA * (v - lp1);
            lp2 += lpA * (lp1 - lp2);
            x[i] = (float)(lp2 + Gauss(rng) * hiss);
        }
        return x;
    }

    /// <summary>Median in-band level (dBFS, decoder units) measured on the reference bins.</summary>
    public static double InBandDb(float[] x)
    {
        var hann = Goertzel.Hann(P.WindowSamples);
        double sum = hann.Sum(v => (double)v);
        var levels = new List<double>();
        for (int i = 0; i + P.WindowSamples <= x.Length; i += P.SampleRate / 10)
        {
            var seg = x.AsSpan(i, P.WindowSamples);
            double a0 = Goertzel.Amplitude(seg, P.ReferenceHz[0], P.SampleRate, hann, sum);
            double a1 = Goertzel.Amplitude(seg, P.ReferenceHz[1], P.SampleRate, hann, sum);
            levels.Add(Goertzel.ToDb(Math.Sqrt((a0 * a0 + a1 * a1) / 2)));
        }
        levels.Sort();
        return levels[levels.Count / 2];
    }

    /// <summary>Scale a signal so its median in-band level equals <paramref name="targetDb"/>.</summary>
    public static float[] ScaleInBand(float[] x, double targetDb)
    {
        float g = Gain(InBandDb(x), targetDb);
        for (int i = 0; i < x.Length; i++) x[i] *= g;
        return x;
    }

    private static double Gauss(Random r)
    {
        double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    public static (List<DecodedCommand> ok, List<RejectedFrame> bad) Run(float[] signal, int blockSize = 480, Random? jitter = null)
    {
        var (ok, _, bad) = RunAll(signal, blockSize, jitter);
        return (ok, bad);
    }

    public static (List<DecodedCommand> ok, List<DecodedZone> zones, List<RejectedFrame> bad) RunAll(float[] signal, int blockSize = 480, Random? jitter = null)
    {
        var d = new FrameDecoder(P);
        var ok = new List<DecodedCommand>();
        var zones = new List<DecodedZone>();
        var bad = new List<RejectedFrame>();
        d.Decoded += ok.Add;
        d.ZoneDecoded += zones.Add;
        d.Rejected += bad.Add;
        int i = 0;
        while (i < signal.Length)
        {
            int n = jitter is null ? blockSize : jitter.Next(1, blockSize * 2);
            n = Math.Min(n, signal.Length - i);
            d.Feed(signal.AsSpan(i, n));
            i += n;
        }
        return (ok, zones, bad);
    }

    public static IEnumerable<string> Fixtures()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.wav") : [];
    }
}
