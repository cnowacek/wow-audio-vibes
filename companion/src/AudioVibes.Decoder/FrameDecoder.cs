namespace AudioVibes.Decoder;

public sealed record DecodedCommand(int Code, string Name, double ConfidenceDb, long SampleIndex, TimeSpan Timestamp);

/// <summary>A complete zone id (two zone frames: high byte then low byte).</summary>
public sealed record DecodedZone(int RawId, bool IsInstance, int Id, double ConfidenceDb, long SampleIndex, TimeSpan Timestamp)
{
    /// <summary>Stable key used in config and defaults, e.g. "map:1429" or "instance:36".</summary>
    public string Key => (IsInstance ? "instance:" : "map:") + Id;
}

public sealed record RejectedFrame(string Reason, long SampleIndex, TimeSpan Timestamp);

/// <summary>Per-hop levels for the debug window (dBFS). Order: see <see cref="FrameDecoder.LevelLabels"/>.</summary>
public sealed record HopLevels(double[] LevelsDb, double NoiseFloorDb, bool PreambleHit, long SampleIndex);

public enum DecoderState { Idle, Preamble, Symbols, Lockout }

/// <summary>
/// Streaming decoder. Feed mono blocks of any size at the protocol sample rate.
/// State machine: Idle → Preamble → Symbols → Emit/Reject → Idle.
/// Two frame kinds share the data band and are told apart by their preamble chord.
/// Hop timing uses absolute sample indices, so block boundaries don't matter.
/// Mirror of generator/vibes_proto.py FrameDecoder; keep them in sync.
/// </summary>
public sealed class FrameDecoder
{
    private readonly ProtocolSpec _p;
    private readonly float[] _hann;
    private readonly double _hannSum;
    private readonly float[] _ring;
    private readonly int _ringMask;
    private long _written;
    private long _nextHopStart;
    private readonly Queue<double> _refHist = new();
    private readonly double[] _sortScratch;

    private FrameKind _kind;
    private long _preFirst, _preLast, _frameStart, _ignoreUntil;
    private int _preCount;
    private (FrameKind, int)? _lastKey;
    private long _lastKeyAt = long.MinValue / 2;
    private (int Byte, long At, double Conf)? _zoneHi;

    public DecoderState State { get; private set; } = DecoderState.Idle;
    public event Action<DecodedCommand>? Decoded;
    public event Action<DecodedZone>? ZoneDecoded;
    public event Action<RejectedFrame>? Rejected;
    public event Action<HopLevels>? Levels;

    /// <summary>Index layout of <see cref="HopLevels.LevelsDb"/>: 8 data, 2 command preamble, 2 zone preamble, 2 reference.</summary>
    public string[] LevelLabels { get; }
    public const int LevelCount = 14;

    public FrameDecoder(ProtocolSpec p)
    {
        _p = p;
        _hann = Goertzel.Hann(p.WindowSamples);
        _hannSum = _hann.Sum(x => (double)x);
        int size = 1;
        while (size < Math.Max(p.SampleRate * 2, p.FrameSamplesFor(FrameKind.Zone) * 4)) size <<= 1;
        _ring = new float[size];
        _ringMask = size - 1;
        _sortScratch = new double[p.NoiseMedianHops];
        LevelLabels = LabelsFor(p);
    }

    public static string[] LabelsFor(ProtocolSpec p) =>
        p.DataHz.Select((f, k) => $"D{k}\n{f / 1000:0.0}k")
            .Concat(p.PreambleHz.Select(f => $"P\n{f / 1000:0.0}k"))
            .Concat(p.ZonePreambleHz.Select(f => $"Z\n{f / 1000:0.0}k"))
            .Concat(p.ReferenceHz.Select(f => $"ref\n{f / 1000:0.0}k")).ToArray();

    public long SamplesConsumed => _written;

    private TimeSpan T(long sample) => TimeSpan.FromSeconds((double)sample / _p.SampleRate);

    public void Feed(ReadOnlySpan<float> block)
    {
        foreach (var s in block)
        {
            _ring[_written & _ringMask] = s;
            _written++;
            if (_written == _nextHopStart + _p.WindowSamples)
            {
                ProcessHop(_nextHopStart);
                _nextHopStart += _p.HopSamples;
            }
            if (State == DecoderState.Symbols && _written == _frameStart + _p.FrameSamplesFor(_kind))
                FinishFrame();
        }
    }

    public void Reset()
    {
        _written = 0; _nextHopStart = 0; _refHist.Clear(); State = DecoderState.Idle;
        _ignoreUntil = 0; _lastKey = null; _zoneHi = null;
    }

    private void Copy(long start, Span<float> dst)
    {
        for (int i = 0; i < dst.Length; i++) dst[i] = _ring[(start + i) & _ringMask];
    }

    private double LevelDb(ReadOnlySpan<float> x, double f, bool windowed) =>
        Goertzel.ToDb(windowed ? Goertzel.Amplitude(x, f, _p.SampleRate, _hann, _hannSum)
                               : Goertzel.Amplitude(x, f, _p.SampleRate, null, x.Length));

    private void ProcessHop(long start)
    {
        Span<float> x = stackalloc float[_p.WindowSamples];
        Copy(start, x);

        var levels = new double[LevelCount];
        for (int k = 0; k < 8; k++) levels[k] = LevelDb(x, _p.DataHz[k], true);
        levels[8] = LevelDb(x, _p.PreambleHz[0], true);
        levels[9] = LevelDb(x, _p.PreambleHz[1], true);
        levels[10] = LevelDb(x, _p.ZonePreambleHz[0], true);
        levels[11] = LevelDb(x, _p.ZonePreambleHz[1], true);
        double r0 = Goertzel.Amplitude(x, _p.ReferenceHz[0], _p.SampleRate, _hann, _hannSum);
        double r1 = Goertzel.Amplitude(x, _p.ReferenceHz[1], _p.SampleRate, _hann, _hannSum);
        levels[12] = Goertzel.ToDb(r0);
        levels[13] = Goertzel.ToDb(r1);
        double refDb = Goertzel.ToDb(Math.Sqrt((r0 * r0 + r1 * r1) / 2));

        _refHist.Enqueue(refDb);
        if (_refHist.Count > _p.NoiseMedianHops) _refHist.Dequeue();
        double floor = Math.Max(Math.Max(Median(), refDb), _p.AbsoluteFloorDbfs);
        double thr = floor + _p.MinPreambleDb;
        double cmdMin = Math.Min(levels[8], levels[9]), zoneMin = Math.Min(levels[10], levels[11]);
        bool cmdHit = cmdMin >= thr, zoneHit = zoneMin >= thr;
        FrameKind? hit = null;
        if (cmdHit || zoneHit) hit = cmdHit && (!zoneHit || cmdMin >= zoneMin) ? FrameKind.Command : FrameKind.Zone;
        if (start < _ignoreUntil) hit = null;

        Levels?.Invoke(new HopLevels(levels, floor, hit != null, start));

        switch (State)
        {
            case DecoderState.Idle:
                if (hit is { } k0) { State = DecoderState.Preamble; _kind = k0; _preFirst = _preLast = start; _preCount = 1; }
                break;
            case DecoderState.Preamble:
                if (hit == _kind)
                {
                    _preCount++; _preLast = start;
                    if (_preCount > _p.MaxPreambleHops)
                    {
                        State = DecoderState.Lockout;
                        Rejected?.Invoke(new RejectedFrame("preamble too long", start, T(start)));
                    }
                }
                else if (_preCount >= _p.MinPreambleHops)
                {
                    _frameStart = Sync();
                    State = DecoderState.Symbols;
                    if (_written >= _frameStart + _p.FrameSamplesFor(_kind)) FinishFrame();
                }
                else if (hit is { } k1) { _kind = k1; _preFirst = _preLast = start; _preCount = 1; }
                else State = DecoderState.Idle;
                break;
            case DecoderState.Lockout:
                if (hit == null) State = DecoderState.Idle;
                break;
        }
    }

    private double Median()
    {
        int n = _refHist.Count;
        _refHist.CopyTo(_sortScratch, 0);
        Array.Sort(_sortScratch, 0, n);
        return n % 2 == 1 ? _sortScratch[n / 2] : 0.5 * (_sortScratch[n / 2 - 1] + _sortScratch[n / 2]);
    }

    /// <summary>Matched-filter search (rectangular window, preamble length) in 1 ms steps for the
    /// exact preamble start. Rect⊛rect peaks sharply at alignment.</summary>
    private long Sync()
    {
        int preN = _p.PreambleSamples;
        int step = Math.Max(1, _p.SampleRate / 1000);
        long lo = Math.Max(Math.Max(0, _written - _ring.Length + 1), _preFirst - preN);
        long hi = _preLast + _p.WindowSamples;
        var seg = new float[preN];
        var tones = _p.PreambleFor(_kind);
        double best = -1; long bestC = lo;
        for (long c = lo; c <= hi && c + preN <= _written; c += step)
        {
            Copy(c, seg);
            double score = 0;
            foreach (var f in tones) score += Goertzel.Amplitude(seg, f, _p.SampleRate, null, preN);
            if (score > best) { best = score; bestC = c; }
        }
        return bestC;
    }

    private void FinishFrame()
    {
        int preN = _p.PreambleSamples, g = _p.GuardSamples, s = _p.SymbolSamples, win = _p.WindowSamples;
        var kind = _kind;
        int n = _p.SymbolCount(kind);
        long end = _frameStart + _p.FrameSamplesFor(kind);
        State = DecoderState.Idle;
        _ignoreUntil = end;

        Span<float> x = stackalloc float[win];
        var syms = new int[n];
        double minMargin = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            long center = _frameStart + preN + g + i * (g + s) + s / 2;
            Copy(center - win / 2, x);
            double best = double.MinValue, second = double.MinValue; int arg = 0;
            for (int k = 0; k < 8; k++)
            {
                double l = LevelDb(x, _p.DataHz[k], true);
                if (l > best) { second = best; best = l; arg = k; }
                else if (l > second) second = l;
            }
            syms[i] = arg;
            minMargin = Math.Min(minMargin, best - second);
        }

        if (minMargin < _p.MinSymbolMarginDb)
        {
            Reject($"{(kind == FrameKind.Zone ? "zone " : "")}symbol margin {minMargin:F1} dB");
            return;
        }
        if (kind == FrameKind.Command) FinishCommand(syms, minMargin);
        else FinishZone(syms, minMargin);
    }

    private void Reject(string why) => Rejected?.Invoke(new RejectedFrame(why, _frameStart, T(_frameStart)));

    private bool Debounced(FrameKind kind, int value)
    {
        long debounce = (long)(_p.DebounceMs * _p.SampleRate / 1000);
        if (_lastKey == (kind, value) && _frameStart - _lastKeyAt < debounce) return true;
        _lastKey = (kind, value); _lastKeyAt = _frameStart;
        return false;
    }

    private void FinishCommand(int[] syms, double margin)
    {
        if (_p.Checksum(syms[0], syms[1]) != syms[2]) { Reject($"checksum ({syms[0]},{syms[1]},{syms[2]})"); return; }
        int code = syms[0] * 8 + syms[1];
        if (_p.ReservedCodes.Contains(code)) { Reject("reserved code"); return; }
        if (Debounced(FrameKind.Command, code)) return;
        Decoded?.Invoke(new DecodedCommand(code, _p.NameOf(code), margin, _frameStart, T(_frameStart)));
    }

    private void FinishZone(int[] syms, double margin)
    {
        if (_p.ZoneChecksum(syms[0], syms[1], syms[2]) != syms[3]) { Reject("zone checksum"); return; }
        int v = (syms[0] << 6) | (syms[1] << 3) | syms[2];
        int part = v >> 8, b = v & 0xFF;
        if (Debounced(FrameKind.Zone, v)) return;
        if (part == 0)
        {
            _zoneHi = (b, _frameStart, margin);
            return;
        }
        long window = (long)(_p.ZonePairWindowMs * _p.SampleRate / 1000);
        if (_zoneHi is { } hi && _frameStart - hi.At <= window)
        {
            _zoneHi = null;
            int raw = (hi.Byte << 8) | b;
            bool inst = (raw & _p.InstanceFlag) != 0;
            ZoneDecoded?.Invoke(new DecodedZone(raw, inst, raw & (_p.InstanceFlag - 1), Math.Min(margin, hi.Conf), _frameStart, T(_frameStart)));
        }
        else
        {
            _zoneHi = null;
            Reject("zone low byte without high byte");
        }
    }
}
