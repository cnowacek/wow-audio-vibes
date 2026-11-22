using Xunit;
using Xunit.Abstractions;
using static AudioVibes.Decoder.Tests.TestSignals;

namespace AudioVibes.Decoder.Tests;

public class ZoneTests(ITestOutputHelper log)
{
    [Fact]
    public void ZoneChecksumCatchesEverySingleSymbolError()
    {
        for (int v = 0; v < 512; v++)
        {
            int[] z = [(v >> 6) & 7, (v >> 3) & 7, v & 7];
            int c = P.ZoneChecksum(z[0], z[1], z[2]);
            for (int i = 0; i < 3; i++)
                for (int e = 1; e < 8; e++)
                {
                    var zz = (int[])z.Clone();
                    zz[i] = (zz[i] + e) % 8;
                    Assert.NotEqual(c, P.ZoneChecksum(zz[0], zz[1], zz[2]));
                }
        }
        Assert.Equal(P.Samples(180), P.FrameSamplesFor(FrameKind.Zone));
    }

    [Fact]
    public void Every512ZoneFileDecodesAsPartOfAPair()
    {
        // Pair every high byte with a fixed low byte and every low byte with a fixed high byte:
        // that exercises all 512 files through the real pairing path.
        int failures = 0;
        for (int b = 0; b < 256; b++)
        {
            foreach (int id in new[] { (b << 8) | 0x95, (0x05 << 8) | b })
            {
                var sig = new float[P.SampleRate];
                MixZone(sig, id, 2400);
                var (cmds, zones, bad) = RunAll(sig);
                if (cmds.Count != 0 || zones.Count != 1 || zones[0].RawId != id)
                {
                    failures++;
                    log.WriteLine($"zone {id:x4}: zones=[{string.Join(",", zones.Select(z => z.RawId.ToString("x4")))}] rejected=[{string.Join(";", bad.Select(r => r.Reason))}]");
                }
            }
        }
        Assert.Equal(0, failures);
    }

    [Fact]
    public void ZoneIdsDecodeWithKindAndId()
    {
        int[] ids = [1429, 1453, 0x8000 | 36, 2548, 0x8000 | 533];
        var sig = new float[P.SampleRate * 5];
        int at = 2400;
        foreach (var id in ids)
        {
            MixZone(sig, id, at);
            MixInto(sig, Chirp(48), at + P.SampleRate / 2, 1f); // EVT_QUEUE_POP right after
            at += P.SampleRate * 3 / 4 + P.SampleRate / 4;
        }
        var (cmds, zones, bad) = RunAll(sig, 777);
        Assert.Empty(bad);
        Assert.Equal(ids, zones.Select(z => z.RawId));
        Assert.Equal(["map:1429", "map:1453", "instance:36", "map:2548", "instance:533"], zones.Select(z => z.Key));
        Assert.Equal(Enumerable.Repeat(48, ids.Length), cmds.Select(c => c.Code));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(481)]
    [InlineData(4096)]
    public void ZoneFramesStraddlingBlockBoundariesDecode(int block)
    {
        var rng = new Random(block);
        for (int offset = 0; offset < 480; offset += 61)
        {
            var sig = new float[P.SampleRate];
            MixZone(sig, 1429, 3000 + offset);
            var (_, zones, _) = RunAll(sig, block, block > 1 ? rng : null);
            Assert.Equal([1429], zones.Select(z => z.RawId));
        }
    }

    [Fact]
    public void LoneLowByteOrStaleHighByteIsNotAZone()
    {
        var sig = new float[P.SampleRate * 3];
        MixInto(sig, ZoneChirp(1, 0x95), 2400, 1f);                         // low byte alone
        MixInto(sig, ZoneChirp(0, 0x05), P.SampleRate, 1f);                 // high byte ...
        MixInto(sig, ZoneChirp(1, 0x95), P.SampleRate * 2, 1f);             // ... low byte 1 s later (> pair window)
        var (_, zones, bad) = RunAll(sig);
        Assert.Empty(zones);
        Assert.Equal(2, bad.Count(b => b.Reason.Contains("without high byte")));
    }

    [Fact]
    public void ZonesDecodeInSyntheticGameplay()
    {
        var noise = ScaleInBand(SyntheticGameplay(P.SampleRate * 60, seed: 21), -85);
        var rng = new Random(5);
        var sent = new List<int>();
        for (int at = P.SampleRate / 2; at + P.SampleRate < noise.Length; at += P.SampleRate)
        {
            int id = rng.Next(2) == 0 ? rng.Next(1400, 2600) : 0x8000 | rng.Next(30, 600);
            sent.Add(id);
            MixZone(noise, id, at);
        }
        var (cmds, zones, _) = RunAll(noise, 960);
        int hits = zones.Count(z => sent.Contains(z.RawId));
        log.WriteLine($"zones in -85 dBFS in-band noise: {hits}/{sent.Count}, {zones.Count - hits} wrong, {cmds.Count} stray commands");
        Assert.Equal(zones.Count, hits);
        Assert.Empty(cmds);
        Assert.True(hits >= sent.Count * 0.95, $"{hits}/{sent.Count}");
    }

    [Fact]
    public void NoFalseZonesInTenMinutesOfSyntheticGameplay()
    {
        var noise = SyntheticGameplay(P.SampleRate * 600, seed: 13, hissRmsDb: -50);
        var (cmds, zones, _) = RunAll(noise, 4800);
        log.WriteLine($"10 min synthetic: {zones.Count} false zones, {cmds.Count} false commands");
        Assert.Empty(zones);
        Assert.Empty(cmds);
    }
}
