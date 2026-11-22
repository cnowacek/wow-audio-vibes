// Milestone 3/4 console tool.
//   vibes-cli levels  [--pid N | --name Wow.exe]   live bin levels from WoW's audio
//   vibes-cli decode  [--pid N | --name Wow.exe]   print decoded commands (end-to-end test)
//   vibes-cli record  out.wav [seconds] [--pid N]  record WoW audio for test fixtures
//   vibes-cli file    in.wav                       decode a WAV offline
using System.Diagnostics;
using AudioVibes.Audio;
using AudioVibes.Decoder;

var p = ProtocolSpec.LoadDefault();
if (args.Length == 0) { Usage(); return 1; }
string mode = args[0];

if (mode == "file")
{
    var (x, rate) = WavFile.ReadMono(args[1]);
    if (rate != p.SampleRate) { Console.Error.WriteLine($"WAV is {rate} Hz; need {p.SampleRate}"); return 1; }
    var d = new FrameDecoder(p);
    int n = 0;
    d.Decoded += c => { n++; Console.WriteLine($"{c.Timestamp:mm\\:ss\\.fff}  {c.Name,-22} ({c.Code})  margin {c.ConfidenceDb:F1} dB"); };
    d.ZoneDecoded += z => { n++; Console.WriteLine($"{z.Timestamp:mm\\:ss\\.fff}  ZONE {z.Key,-17}  margin {z.ConfidenceDb:F1} dB"); };
    d.Rejected += r => Console.WriteLine($"{r.Timestamp:mm\\:ss\\.fff}  rejected: {r.Reason}");
    d.Feed(x);
    Console.WriteLine($"{n} decoded in {x.Length / (double)rate:F1} s");
    return 0;
}

if (!ProcessLoopbackCapture.IsSupported) { Console.Error.WriteLine(ProcessLoopbackCapture.SupportMessage); return 1; }
int pid = FindPid(args);
if (pid == 0) { Console.Error.WriteLine("WoW process not found (use --pid or --name)"); return 1; }

using var cap = new ProcessLoopbackCapture(pid, p.SampleRate);
var dec = new FrameDecoder(p);
using var done = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };

switch (mode)
{
    case "levels":
    {
        double[] peak = new double[FrameDecoder.LevelCount]; Array.Fill(peak, -200); double floor = 0; bool hit = false;
        dec.Levels += l => { lock (peak) { for (int i = 0; i < FrameDecoder.LevelCount; i++) peak[i] = Math.Max(peak[i], l.LevelsDb[i]); floor = l.NoiseFloorDb; hit |= l.PreambleHit; } };
        dec.Decoded += c => Console.WriteLine($"\n>>> {c.Name} ({c.Code}) margin {c.ConfidenceDb:F1} dB");
        cap.SamplesAvailable += b => dec.Feed(b);
        cap.Start();
        Console.WriteLine($"capturing pid {pid}: {cap.FormatDescription}. Peak dBFS per 250 ms (D0..D7 | cmd P0 P1 | zone Z0 Z1 | ref0 ref1). Ctrl+C to stop.");
        while (!done.Wait(250))
            lock (peak)
            {
                Console.WriteLine(string.Join(" ", peak.Take(8).Select(v => $"{v,6:F0}")) + " |" +
                    string.Join(" ", peak.Skip(8).Take(2).Select(v => $"{v,6:F0}")) + " |" +
                    string.Join(" ", peak.Skip(10).Take(2).Select(v => $"{v,6:F0}")) + " |" +
                    string.Join(" ", peak.Skip(12).Select(v => $"{v,6:F0}")) + $"  floor {floor,6:F0}" + (hit ? "  PREAMBLE" : ""));
                Array.Fill(peak, -200); hit = false;
            }
        break;
    }
    case "decode":
    {
        var sw = Stopwatch.StartNew();
        int ok = 0, rej = 0;
        dec.Decoded += c => { ok++; Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {c.Name,-22} ({c.Code})  margin {c.ConfidenceDb:F1} dB"); };
        dec.ZoneDecoded += z => { ok++; Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  ZONE {z.Key,-17}  margin {z.ConfidenceDb:F1} dB"); };
        dec.Rejected += r => { rej++; Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  rejected: {r.Reason}"); };
        cap.SamplesAvailable += b => dec.Feed(b);
        cap.Start();
        Console.WriteLine($"decoding pid {pid} ({cap.FormatDescription}). Ctrl+C to stop.");
        done.Wait();
        Console.WriteLine($"{sw.Elapsed:hh\\:mm\\:ss}: {ok} decoded, {rej} rejected");
        break;
    }
    case "record":
    {
        string path = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : $"wow-{DateTime.Now:yyyyMMdd-HHmmss}.wav";
        double secs = args.Length > 2 && double.TryParse(args[2], out var s) ? s : 60;
        int target = (int)(secs * p.SampleRate);
        var buf = new List<float>(target);
        cap.SamplesAvailable += b => { lock (buf) { if (buf.Count < target) buf.AddRange(b); if (buf.Count >= target) done.Set(); } };
        cap.Start();
        Console.WriteLine($"recording {secs} s from pid {pid} → {path} ...");
        done.Wait();
        lock (buf) WavFile.WriteMonoFloat(path, buf.Take(target).ToArray(), p.SampleRate);
        Console.WriteLine("saved " + Path.GetFullPath(path));
        break;
    }
    default: Usage(); return 1;
}
return 0;

static int FindPid(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--pid") return int.Parse(args[i + 1]);
        if (args[i] == "--name") return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(args[i + 1])).FirstOrDefault()?.Id ?? 0;
    }
    foreach (var n in new[] { "Wow", "WowClassic", "Wow_Forever" })
        if (Process.GetProcessesByName(n).FirstOrDefault() is { } pr) return pr.Id;
    return 0;
}

static void Usage() => Console.WriteLine("""
    vibes-cli levels [--pid N | --name Wow.exe]
    vibes-cli decode [--pid N | --name Wow.exe]
    vibes-cli record [out.wav] [seconds] [--pid N]
    vibes-cli file   in.wav
    """);
