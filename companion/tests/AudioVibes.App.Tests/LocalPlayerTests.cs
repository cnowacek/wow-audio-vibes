using AudioVibes.App;
using NAudio.Wave;
using Xunit;

namespace AudioVibes.App.Tests;

public class LocalPlayerTests
{
    [Theory]
    [InlineData("https://open.spotify.com/playlist/ABC?si=x", "spotify:playlist:ABC")]
    [InlineData("https://ice1.somafm.com/groovesalad-128-mp3", "radio:https://ice1.somafm.com/groovesalad-128-mp3")]
    [InlineData("radio:http://example.com/live", "radio:http://example.com/live")]
    [InlineData(@"C:\Music\Haunted\", @"folder:C:\Music\Haunted")]
    [InlineData("\"D:/Games/Music\"", @"folder:D:\Games\Music")]
    [InlineData(@"folder:\\nas\music", @"folder:\\nas\music")]
    [InlineData("https://www.youtube.com/watch?v=abc", null)]
    [InlineData("https://youtu.be/abc", null)]
    [InlineData("some words", null)]
    [InlineData("Music\\relative", null)]
    public void NormalizesEveryKindOfSource(string input, string? expected) => Assert.Equal(expected, MusicUri.Normalize(input));

    [Fact]
    public void KindsLabelsAndLinks()
    {
        Assert.Equal(MusicKind.Radio, MusicUri.KindOf("radio:https://a/b"));
        Assert.Equal(MusicKind.Folder, MusicUri.KindOf(@"folder:C:\x"));
        Assert.Equal(MusicKind.Spotify, MusicUri.KindOf("spotify:album:1"));
        Assert.Equal("Radio: ice1.somafm.com", MusicUri.Label("radio:https://ice1.somafm.com/doomed-128-mp3"));
        Assert.Equal("Folder: Haunted", MusicUri.Label(@"folder:C:\Music\Haunted"));
        Assert.Equal("https://open.spotify.com/playlist/X", MusicUri.OpenTarget("spotify:playlist:X"));
        Assert.Equal(@"C:\Music", MusicUri.OpenTarget(@"folder:C:\Music"));
    }

    [Fact]
    public void ReadsPlsAndM3u()
    {
        Assert.Equal("http://s1.example.com:8000/live",
            LocalPlayer.FirstStreamInPlaylist("[playlist]\nNumberOfEntries=2\nFile1=http://s1.example.com:8000/live\nTitle1=x\nFile2=http://s2/"));
        Assert.Equal("https://x.example/stream.mp3", LocalPlayer.FirstStreamInPlaylist("#EXTM3U\n#EXTINF:-1,Station\nhttps://x.example/stream.mp3\n"));
        Assert.Null(LocalPlayer.FirstStreamInPlaylist("nothing here"));
    }

    private static string TempFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "av-music-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteTone(string path, int sampleRate, int channels, double seconds, float amp)
    {
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
        int n = (int)(sampleRate * seconds);
        for (int i = 0; i < n; i++)
            for (int c = 0; c < channels; c++)
                w.WriteSample(amp * (float)Math.Sin(2 * Math.PI * 440 * i / sampleRate));
    }

    [Fact]
    public void ListsAudioFilesRecursivelyInOrder()
    {
        var dir = TempFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "b"));
            File.WriteAllText(Path.Combine(dir, "b", "02.mp3"), "");
            File.WriteAllText(Path.Combine(dir, "01.FLAC"), "");
            File.WriteAllText(Path.Combine(dir, "cover.jpg"), "");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "");
            var files = LocalPlayer.ListFolder(dir).Select(f => Path.GetRelativePath(dir, f)).ToList();
            Assert.Equal(["01.FLAC", Path.Combine("b", "02.mp3")], files);
            Assert.Throws<DirectoryNotFoundException>(() => LocalPlayer.ListFolder(Path.Combine(dir, "missing")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task DecodesAFolderTrackToStereo48k()
    {
        var dir = TempFolder();
        try
        {
            // mono 44.1 kHz: exercises mono→stereo and resampling
            WriteTone(Path.Combine(dir, "tone.wav"), 44100, 1, 3, 0.5f);
            var (peak, decoded) = await LocalPlayer.ProbeAsync("folder:" + dir, TimeSpan.FromSeconds(2));
            Assert.InRange(peak, 0.4f, 0.6f);
            Assert.True(decoded >= TimeSpan.FromSeconds(2), $"decoded {decoded}");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task DecodesOggVorbis()
    {
        // The addon's own chirps are Ogg Vorbis: a handy real file to decode.
        var ogg = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "addon", "AudioVibes", "sounds"), "*.ogg").First();
        var dir = TempFolder();
        try
        {
            File.Copy(ogg, Path.Combine(dir, "chirp.ogg"));
            var (peak, decoded) = await LocalPlayer.ProbeAsync("folder:" + dir, TimeSpan.FromMilliseconds(100));
            Assert.True(decoded > TimeSpan.Zero);
            Assert.True(peak > 0.001f, $"peak {peak}");
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Live check of the built-in stations. Off by default (needs the internet); set AUDIOVIBES_NET_TESTS=1.</summary>
    [SkippableFact]
    public async Task BuiltInStationsDecode()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("AUDIOVIBES_NET_TESTS") == "1", "set AUDIOVIBES_NET_TESTS=1 to check the live streams");
        var failures = new List<string>();
        foreach (var (key, st) in ZoneCatalog.Load().Stations)
        {
            try
            {
                var (peak, decoded) = await LocalPlayer.ProbeAsync("radio:" + st.Url, TimeSpan.FromSeconds(2));
                if (decoded < TimeSpan.FromSeconds(1)) failures.Add($"{key}: only {decoded}");
            }
            catch (Exception e) { failures.Add($"{key}: {e.Message}"); }
        }
        Assert.Empty(failures);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "addon"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
