using System.Text.Json;

namespace AudioVibes.Decoder;

public enum FrameKind { Command, Zone }

/// <summary>Parsed protocol/protocol.json. Loaded from a file next to the exe if present
/// (so you can retune without rebuilding), otherwise from the copy embedded at build time.</summary>
public sealed class ProtocolSpec
{
    public int Version { get; init; }
    public int SampleRate { get; init; }
    public double[] DataHz { get; init; } = [];
    public double[] PreambleHz { get; init; } = [];
    public double[] ReferenceHz { get; init; } = [];
    public double PreambleMs { get; init; }
    public double SymbolMs { get; init; }
    public double GuardMs { get; init; }
    public double AmplitudeDbfs { get; init; }
    public int ChkA { get; init; }
    public int ChkB { get; init; }
    public int ChkC { get; init; }
    public HashSet<int> ReservedCodes { get; init; } = [];

    // Zone frames (see protocol.json "zoneFrame")
    public double[] ZonePreambleHz { get; init; } = [];
    public int[] ZoneChk { get; init; } = [1, 3, 5, 7];
    public double ZonePairWindowMs { get; init; } = 700;
    public int InstanceFlag { get; init; } = 0x8000;

    public int HopSamples { get; init; }
    public int WindowSamples { get; init; }
    public int NoiseMedianHops { get; init; }
    public double MinPreambleDb { get; set; }
    public int MinPreambleHops { get; init; }
    public int MaxPreambleHops { get; init; }
    public double MinSymbolMarginDb { get; set; }
    public double DebounceMs { get; set; }
    public double AbsoluteFloorDbfs { get; init; }

    public IReadOnlyDictionary<string, int> Commands { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<int, string> CodeToName { get; init; } = new Dictionary<int, string>();

    public int Samples(double ms) => (int)Math.Round(ms * SampleRate / 1000.0);
    public int PreambleSamples => Samples(PreambleMs);
    public int SymbolSamples => Samples(SymbolMs);
    public int GuardSamples => Samples(GuardMs);
    public int FrameSamples => FrameSamplesFor(FrameKind.Command);
    public int SymbolCount(FrameKind k) => k == FrameKind.Command ? 3 : 4;
    public int FrameSamplesFor(FrameKind k) => PreambleSamples + SymbolCount(k) * (GuardSamples + SymbolSamples);
    public double[] PreambleFor(FrameKind k) => k == FrameKind.Command ? PreambleHz : ZonePreambleHz;

    public int Checksum(int s0, int s1) => ((ChkA * s0 + ChkB * s1 + ChkC) % 8 + 8) % 8;
    public int ZoneChecksum(int z0, int z1, int z2) =>
        ((ZoneChk[0] * z0 + ZoneChk[1] * z1 + ZoneChk[2] * z2 + ZoneChk[3]) % 8 + 8) % 8;
    public string NameOf(int code) => CodeToName.TryGetValue(code, out var n) ? n : $"CODE_{code}";

    public static ProtocolSpec LoadDefault()
    {
        var side = Path.Combine(AppContext.BaseDirectory, "protocol.json");
        if (File.Exists(side)) return Parse(File.ReadAllText(side));
        using var s = typeof(ProtocolSpec).Assembly.GetManifestResourceStream("AudioVibes.protocol.json")
            ?? throw new InvalidOperationException("embedded protocol.json missing");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    public static ProtocolSpec Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var a = root.GetProperty("audio");
        var dt = a.GetProperty("dataTones");
        double baseHz = dt.GetProperty("baseHz").GetDouble(), step = dt.GetProperty("stepHz").GetDouble();
        int count = dt.GetProperty("count").GetInt32();
        var fr = root.GetProperty("frame");
        var chk = fr.GetProperty("checksum");
        var d = root.GetProperty("decoder");
        var z = root.GetProperty("zoneFrame");
        var zc = z.GetProperty("checksum");
        var cmds = new Dictionary<string, int>();
        foreach (var p in root.GetProperty("commands").EnumerateObject()) cmds[p.Name] = p.Value.GetInt32();

        static double[] Arr(JsonElement e) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        return new ProtocolSpec
        {
            Version = root.GetProperty("version").GetInt32(),
            SampleRate = a.GetProperty("sampleRate").GetInt32(),
            DataHz = Enumerable.Range(0, count).Select(k => baseHz + step * k).ToArray(),
            PreambleHz = Arr(a.GetProperty("preambleHz")),
            ReferenceHz = Arr(a.GetProperty("referenceHz")),
            PreambleMs = a.GetProperty("preambleMs").GetDouble(),
            SymbolMs = a.GetProperty("symbolMs").GetDouble(),
            GuardMs = a.GetProperty("guardMs").GetDouble(),
            AmplitudeDbfs = a.GetProperty("amplitudeDbfs").GetDouble(),
            ChkA = chk.GetProperty("a").GetInt32(),
            ChkB = chk.GetProperty("b").GetInt32(),
            ChkC = chk.GetProperty("c").GetInt32(),
            ReservedCodes = fr.GetProperty("reservedCodes").EnumerateArray().Select(x => x.GetInt32()).ToHashSet(),
            ZonePreambleHz = Arr(z.GetProperty("preambleHz")),
            ZoneChk = [zc.GetProperty("a").GetInt32(), zc.GetProperty("b").GetInt32(), zc.GetProperty("c").GetInt32(), zc.GetProperty("d").GetInt32()],
            ZonePairWindowMs = z.GetProperty("pairWindowMs").GetDouble(),
            InstanceFlag = z.GetProperty("instanceFlag").GetInt32(),
            HopSamples = d.GetProperty("hopSamples").GetInt32(),
            WindowSamples = d.GetProperty("windowSamples").GetInt32(),
            NoiseMedianHops = d.GetProperty("noiseMedianHops").GetInt32(),
            MinPreambleDb = d.GetProperty("minPreambleDb").GetDouble(),
            MinPreambleHops = d.GetProperty("minPreambleHops").GetInt32(),
            MaxPreambleHops = d.GetProperty("maxPreambleHops").GetInt32(),
            MinSymbolMarginDb = d.GetProperty("minSymbolMarginDb").GetDouble(),
            DebounceMs = d.GetProperty("debounceMs").GetDouble(),
            AbsoluteFloorDbfs = d.GetProperty("absoluteFloorDbfs").GetDouble(),
            Commands = cmds,
            CodeToName = cmds.ToDictionary(kv => kv.Value, kv => kv.Key),
        };
    }
}
