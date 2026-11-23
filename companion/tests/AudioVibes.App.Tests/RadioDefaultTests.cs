using AudioVibes.App;
using AudioVibes.Spotify;
using Xunit;

namespace AudioVibes.App.Tests;

public class RadioDefaultTests
{
    private sealed class FakePlayer : ILocalPlayer
    {
        public List<(string Uri, bool Shuffle, string? Track, int Ms)> Played = [];
        public int Stops;
        public string? Uri { get; private set; }
        public bool IsActive => Uri != null;
        public string? NowPlaying => null;
        public (string Track, int Ms)? Position { get; set; }
        public string? FailFor;   // PlayAsync of this uri throws, like an unreachable stream
        public Task PlayAsync(string uri, bool shuffle, string? resumeTrack = null, int resumeMs = 0)
        {
            if (uri == FailFor) throw new IOException("stream unreachable");
            Played.Add((uri, shuffle, resumeTrack, resumeMs)); Uri = uri; return Task.CompletedTask;
        }
        public void Stop() { Stops++; Uri = null; }
        public bool TogglePause() => true;
        public bool Pause() => true;
        public bool Next() => true;
        public bool Previous() => true;
        public void StepVolume(float delta) { }
        public Task DuckAsync(float level) => Task.CompletedTask;
        public Task UnduckAsync() => Task.CompletedTask;
#pragma warning disable CS0067
        public event Action<string>? Error;
        public event Action<string>? TrackChanged;
#pragma warning restore CS0067
    }

    private static (ZoneMusic Music, FakePlayer Player, int[] SpotifyPauses) Make(AppConfig cfg, GameOverrides? game = null)
    {
        var player = new FakePlayer();
        var pauses = new int[1];
        var music = new ZoneMusic(ZoneCatalog.Load(), new SpotifyWebApi(Path.GetTempFileName()), () => cfg, () => game ?? GameOverrides.Empty,
            player, () => { pauses[0]++; return Task.CompletedTask; });
        return (music, player, pauses);
    }

    [Fact]
    public void EveryVibeHasAStationForBothModes()
    {
        var cat = ZoneCatalog.Load();
        Assert.NotEmpty(cat.Stations);
        foreach (var vibe in cat.Vibes.Keys)
            foreach (var mode in Modes.All)
            {
                var st = cat.RadioFor(vibe, mode);
                Assert.True(st != null, $"{vibe}/{mode} has no station");
                Assert.Matches("^https?://", st!.Url);
            }
        // Every backup names a real station, and no station is its own backup.
        foreach (var (key, st) in cat.Stations)
        {
            Assert.True(st.Backup == null || cat.Stations.ContainsKey(st.Backup), $"{key}'s backup {st.Backup} doesn't exist");
            Assert.NotEqual(key, st.Backup);
        }
    }

    [Fact]
    public async Task ADeadStationFallsBackToItsBackup()
    {
        var cat = ZoneCatalog.Load();
        var natam = cat.Stations["nativeamerican"];
        var backup = cat.Stations[natam.Backup!];
        var (music, player, _) = Make(new AppConfig());
        player.FailFor = "radio:" + natam.Url;
        await music.PlayAsync("radio:" + natam.Url, false);
        Assert.Equal("radio:" + backup.Url, player.Uri);
        // A stream that isn't a built-in station has no backup: the error comes through.
        player.FailFor = "radio:https://example.invalid/stream";
        await Assert.ThrowsAsync<IOException>(() => music.PlayAsync("radio:https://example.invalid/stream", false));
    }

    [Fact]
    public async Task WithoutSpotifyDefaultsAreRadio()
    {
        var (music, _, _) = Make(new AppConfig());   // Spotify not set up → auto = radio
        Assert.Equal("radio", music.DefaultSource);
        var orgRelaxed = await music.ChooseAsync("map:1454", null, mode: Modes.Relaxed);
        Assert.Equal(MusicKind.Radio, MusicUri.KindOf(orgRelaxed.Uri));
        Assert.Equal("Radio Caprice Native American", orgRelaxed.PlaylistLabel);
        var barrens = await music.ChooseAsync("map:1413", null, mode: Modes.Relaxed);
        Assert.DoesNotContain("rivendell", barrens.Uri);
        var duskwood = await music.ChooseAsync("map:1431", null, mode: Modes.Locked);
        Assert.Contains("doomed", duskwood.Uri);
    }

    [Fact]
    public async Task SpotifySetUpMeansSpotifyDefaultsAndOffMeansNone()
    {
        var cfg = new AppConfig { Spotify = { UseWebApi = true, ClientId = "abc" } };
        var (music, _, _) = Make(cfg);
        Assert.Equal("spotify", music.DefaultSource);      // the temp token file counts as logged in
        cfg.ZoneMusic.DefaultSource = "radio";
        Assert.Equal("radio", music.DefaultSource);
        cfg.ZoneMusic.DefaultSource = "off";
        Assert.Null((await music.ChooseAsync("map:1454", null, false)).Uri);
    }

    [Fact]
    public async Task OverridesCanBeRadioOrFolders()
    {
        var cfg = new AppConfig();
        cfg.ZonePlaylists["map:1429@relaxed"] = "https://ice1.somafm.com/thistle-128-mp3";
        cfg.ZonePlaylists["map:1429@locked"] = @"D:\Music\Elwynn";
        var (music, _, _) = Make(cfg);
        var r = await music.ChooseAsync("map:1429", null, false, mode: Modes.Relaxed);
        Assert.Equal("radio:https://ice1.somafm.com/thistle-128-mp3", r.Uri);
        Assert.Equal("Radio: ice1.somafm.com", r.PlaylistLabel);
        var l = await music.ChooseAsync("map:1429", null, false, mode: Modes.Locked);
        Assert.Equal(@"folder:D:\Music\Elwynn", l.Uri);
        Assert.Equal("Folder: Elwynn", l.PlaylistLabel);

        // in game: a radio link saved by the addon
        var game = GameOverrides.FromLua("AudioVibesDB = { locations = { [\"map:1436\"] = { relaxed = { uri = \"radio:https://relay.rainwave.cc/game.mp3\", name = \"Game radio\" } } } }");
        Assert.Equal("radio:https://relay.rainwave.cc/game.mp3", game.Locations["map:1436"].Relaxed!.Uri);
    }

    [Fact]
    public async Task PlayingRadioUsesTheLocalPlayerAndPausesSpotify()
    {
        var cfg = new AppConfig { ZoneMusic = { DecisionDelayMs = 0 } };
        var (music, player, pauses) = Make(cfg);
        await music.PlayManualAsync("radio:https://relay.rainwave.cc/game.mp3");
        Assert.Single(player.Played);
        Assert.Equal(1, pauses[0]);
        Assert.True(player.IsActive);
    }

    [Fact]
    public async Task FoldersResumeWhereTheyLeftOff()
    {
        var (music, player, _) = Make(new AppConfig());
        await music.PlayAsync(@"folder:C:\Music\A", false);
        player.Position = (@"C:\Music\A\03.mp3", 61_000);
        await music.PlayAsync(@"folder:C:\Music\B", false);   // leaving A remembers where it was
        await music.PlayAsync(@"folder:C:\Music\A", false);   // coming back resumes
        var back = player.Played[^1];
        Assert.Equal(@"folder:C:\Music\A", back.Uri);
        Assert.Equal(@"C:\Music\A\03.mp3", back.Track);
        Assert.Equal(61_000, back.Ms);
    }
}
