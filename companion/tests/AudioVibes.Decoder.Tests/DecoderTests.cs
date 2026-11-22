using AudioVibes.Decoder;
using Xunit;
using Xunit.Abstractions;
using static AudioVibes.Decoder.Tests.TestSignals;

namespace AudioVibes.Decoder.Tests;

public class DecoderTests(ITestOutputHelper log)
{
    public static IEnumerable<object[]> AllCodes() =>
        Enumerable.Range(1, 63).Select(c => new object[] { c });

    [Theory, MemberData(nameof(AllCodes))]
    public void CleanFrameDecodes(int code)
    {
        var sig = new float[P.SampleRate / 2];
        MixInto(sig, Chirp(code), 4800, 1f);
        var (ok, bad) = Run(sig);
        Assert.Empty(bad);
        var d = Assert.Single(ok);
        Assert.Equal(code, d.Code);
        Assert.Equal(P.NameOf(code), d.Name);
        Assert.True(d.ConfidenceDb > 20, $"confidence {d.ConfidenceDb:F1}");
    }

    [Fact]
    public void ChecksumMatchesSpec()
    {
        for (int a = 0; a < 8; a++)
            for (int b = 0; b < 8; b++)
                Assert.Equal((a + 3 * b + 5) % 8, P.Checksum(a, b));
        Assert.Equal(145 * P.SampleRate / 1000, P.FrameSamples);
        Assert.Contains(0, P.ReservedCodes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(480)]
    [InlineData(481)]
    [InlineData(4096)]
    public void FramesStraddlingBlockBoundariesDecode(int block)
    {
        var rng = new Random(block);
        for (int offset = 0; offset < 480; offset += 37)
        {
            var sig = new float[P.SampleRate / 2];
            MixInto(sig, Chirp(45), 3000 + offset, 1f);
            var (ok, _) = Run(sig, block, block > 1 ? rng : null);
            Assert.Equal([45], ok.Select(o => o.Code));
        }
    }

    [Fact]
    public void BackToBackFrames250msApart()
    {
        int[] codes = [2, 3, 2, 9, 45, 10, 63, 1];
        var sig = new float[P.SampleRate * 3];
        for (int i = 0; i < codes.Length; i++)
            MixInto(sig, Chirp(codes[i]), 2400 + i * P.SampleRate / 4, 1f);
        var (ok, bad) = Run(sig, 1024);
        Assert.Empty(bad);
        Assert.Equal(codes, ok.Select(o => o.Code));
    }

    [Fact]
    public void IdenticalCodeWithin500msIsDebounced()
    {
        var sig = new float[P.SampleRate * 2];
        MixInto(sig, Chirp(2), 2400, 1f);
        MixInto(sig, Chirp(2), 2400 + P.SampleRate / 4, 1f);   // 250 ms later → dropped
        MixInto(sig, Chirp(2), 2400 + P.SampleRate * 3 / 4, 1f); // 750 ms later → kept
        var (ok, _) = Run(sig);
        Assert.Equal(2, ok.Count);
    }

    [Fact]
    public void ContinuousPreambleToneIsRejected()
    {
        var sig = new float[P.SampleRate];
        var amp = (float)Math.Pow(10, -51 / 20.0);
        for (int i = 0; i < sig.Length; i++)
            foreach (var f in P.PreambleHz) sig[i] += amp * (float)Math.Sin(2 * Math.PI * f * i / P.SampleRate);
        var (ok, bad) = Run(sig);
        Assert.Empty(ok);
        Assert.Contains(bad, b => b.Reason.Contains("too long"));
    }

    /// <summary>
    /// Noise is pinned to an explicit in-band level (median of the 15.5/19.6 kHz reference bins), so results
    /// read as SNR. -85 dBFS in-band is a guess at a typical game mix; real fixtures replace the guess.
    /// Preamble tones sit 6 dB below the frame peak, so a -45 dBFS frame has -51 dBFS preamble tones.
    /// </summary>
    [Theory]
    [InlineData(false, -85.0, 95.0)] // steady mix: ≥95% at the shipping level
    [InlineData(true, -85.0, 80.0)]  // + loud broadband bursts (spell impacts) that physically mask some chirps
    [InlineData(false, -70.0, 0.0)]  // harsh HF-heavy mix: reported only, shows where the band runs out
    public void DetectionRateInSyntheticGameplay(bool bursts, double inBandDb, double minRate)
    {
        // Lower levels are reported for tuning only; wrong codes are never allowed.
        var noise = ScaleInBand(SyntheticGameplay(P.SampleRate * 60, seed: 7, bursts: bursts), inBandDb);
        log.WriteLine($"bursts={bursts}, in-band noise {InBandDb(noise):F1} dBFS");
        foreach (var level in new[] { -45.0, -55.0, -65.0 })
        {
            var sig = (float[])noise.Clone();
            var rng = new Random(11);
            var sent = new List<int>();
            for (int at = P.SampleRate / 2; at + P.SampleRate < sig.Length; at += P.SampleRate * 6 / 10)
            {
                int code = rng.Next(1, 64);
                if (sent.Count > 0 && sent[^1] == code) code = code % 63 + 1;
                sent.Add(code);
                MixInto(sig, Chirp(code), at, Gain(P.AmplitudeDbfs, level));
            }
            var (ok, bad) = Run(sig, 960);
            int hits = 0, j = 0;
            foreach (var d in ok)
            {
                while (j < sent.Count && sent[j] != d.Code) j++;
                if (j < sent.Count) { hits++; j++; }
            }
            int wrong = ok.Count - hits;
            double rate = 100.0 * hits / sent.Count;
            log.WriteLine($"{level} dBFS: {hits}/{sent.Count} = {rate:F1}% detected, {wrong} wrong codes, {bad.Count} rejected");
            Assert.Equal(0, wrong);
            if (level >= -45) Assert.True(rate >= minRate, $"detection {rate:F1}% at {level} dBFS");
        }
    }

    [Fact]
    public void NoFalsePositivesInTenMinutesOfSyntheticGameplay()
    {
        var noise = SyntheticGameplay(P.SampleRate * 600, seed: 3, hissRmsDb: -50);
        var (ok, bad) = Run(noise, 4800);
        log.WriteLine($"10 min synthetic (in-band {InBandDb(noise):F1} dBFS, with bursts): {ok.Count} false positives, {bad.Count} rejected candidates");
        Assert.Empty(ok);
    }

    // ---------- Real recorded gameplay (tests/fixtures/*.wav, captured with the debug window) ----------

    [SkippableFact]
    public void FixtureDetectionRates()
    {
        var files = Fixtures().ToList();
        Skip.If(files.Count == 0, "no gameplay fixtures in tests/fixtures yet (record them from the debug window)");
        foreach (var file in files)
        {
            var (noise, rate) = WavFile.ReadMono(file);
            Skip.If(rate != P.SampleRate, $"{file} is {rate} Hz");
            foreach (var level in new[] { -45.0, -55.0, -65.0 })
            {
                var sig = (float[])noise.Clone();
                int sent = 0;
                for (int at = P.SampleRate / 2; at + P.SampleRate < sig.Length; at += P.SampleRate) { MixInto(sig, Chirp(2 + sent % 2), at, Gain(P.AmplitudeDbfs, level)); sent++; }
                var (ok, _) = Run(sig, 960);
                log.WriteLine($"{Path.GetFileName(file)} {level} dBFS: {ok.Count}/{sent} = {100.0 * ok.Count / sent:F1}%");
            }
        }
    }

    [SkippableFact]
    public void FixtureNoFalsePositives()
    {
        var files = Fixtures().ToList();
        Skip.If(files.Count == 0, "no gameplay fixtures in tests/fixtures yet");
        double seconds = 0; int fp = 0;
        foreach (var file in files)
        {
            var (x, rate) = WavFile.ReadMono(file);
            seconds += (double)x.Length / rate;
            var (ok, _) = Run(x, 960);
            foreach (var d in ok) log.WriteLine($"FALSE POSITIVE {Path.GetFileName(file)} @ {d.Timestamp}: {d.Name}");
            fp += ok.Count;
        }
        log.WriteLine($"{seconds / 60:F1} min of real gameplay, {fp} false positives (bar: 0 over 10+ min)");
        Assert.Equal(0, fp);
    }
}
