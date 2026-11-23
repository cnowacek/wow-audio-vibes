using AudioVibes.App;
using AudioVibes.Spotify;
using Xunit;
using E = AudioVibes.App.ResolvedLua.Entry;
using ME = AudioVibes.App.ResolvedLua.ModeEntries;

namespace AudioVibes.App.Tests;

public class ResolvedLuaTests
{
    [Fact]
    public void RendersValidLuaWithBothModesAndEscapedNames()
    {
        var lua = ResolvedLua.Render(
            new Dictionary<string, ME>
            {
                ["map:1429"] = new(new E("spotify:playlist:GOLD", "Goldshire \"Hearth\" \\ tavern"), new E("spotify:playlist:ROAD", null)),
                ["instance:36"] = new(null, new E("spotify:playlist:VC", "Van Cleef")),
                ["map:1"] = new(new E(null, "no uri: skipped"), null),
            },
            new Dictionary<string, ME> { ["raid"] = new(null, new E("spotify:album:RAID", "Raid night")) },
            new DateTime(2026, 11, 22, 12, 0, 0));

        var db = (LuaSavedVariables.LuaTable)LuaSavedVariables.Parse(lua)["AudioVibesResolved"]!;
        var locs = db.Table("locations")!;
        Assert.Equal(2, locs.Count);
        var elwynn = locs.Table("map:1429")!;
        Assert.Equal("Goldshire \"Hearth\" \\ tavern", elwynn.Table("relaxed")!.Str("name"));
        Assert.Equal("spotify:playlist:ROAD", elwynn.Table("locked")!.Str("uri"));
        Assert.Null(elwynn.Table("locked")!.Str("name"));
        Assert.Null(locs.Table("instance:36")!.Table("relaxed"));
        Assert.Equal("spotify:album:RAID", db.Table("types")!.Table("raid")!.Table("locked")!.Str("uri"));
    }

    [Fact]
    public async Task UsesCompanionChoicesPerModeButNotInGameOnes()
    {
        var cfg = new AppConfig();
        cfg.ZoneMusic.DefaultSource = "spotify";   // unresolved Spotify defaults are left out
        cfg.ZonePlaylists["map:1429"] = "https://open.spotify.com/playlist/ELWYNN";            // both modes
        cfg.ZonePlaylists["map:1454@relaxed"] = "spotify:playlist:SLOWORG";                     // Orgrimmar, relaxed only
        cfg.VibePlaylists["dungeon@locked"] = "spotify:playlist:DUNGEONS";
        var game = GameOverrides.FromLua("AudioVibesDB = { locations = { [\"map:1436\"] = { uri = \"spotify:playlist:WESTFALL\" } } }");
        var music = new ZoneMusic(ZoneCatalog.Load(), new SpotifyWebApi(Path.GetTempFileName()), () => cfg, () => game);

        var (lua, count) = await ResolvedLua.BuildAsync(music);
        var locs = ((LuaSavedVariables.LuaTable)LuaSavedVariables.Parse(lua)["AudioVibesResolved"]!).Table("locations")!;
        Assert.Equal("spotify:playlist:ELWYNN", locs.Table("map:1429")!.Table("relaxed")!.Str("uri"));
        Assert.Equal("spotify:playlist:ELWYNN", locs.Table("map:1429")!.Table("locked")!.Str("uri"));
        Assert.Equal("spotify:playlist:SLOWORG", locs.Table("map:1454")!.Table("relaxed")!.Str("uri"));
        Assert.Null(locs.Table("map:1454")!.Table("locked"));                                   // not resolved, no override
        Assert.Equal("spotify:playlist:DUNGEONS", locs.Table("instance:36")!.Table("locked")!.Str("uri"));
        // Westfall's in-game playlist is the addon's own business; the companion only reports its own choices.
        Assert.Null(locs.Table("map:1436"));
        Assert.True(count >= 20);
        // The music setup, so the addon knows what can work: Spotify chosen, but not connected here.
        var src = ((LuaSavedVariables.LuaTable)LuaSavedVariables.Parse(lua)["AudioVibesResolved"]!).Table("source")!;
        Assert.Equal("spotify", src.Str("default"));
        Assert.DoesNotContain("spotify = true", lua);
    }

    [Fact]
    public async Task ReportsRadioAsTheDefaultWithoutSpotify()
    {
        var cfg = new AppConfig();   // defaultSource auto, no Spotify set up → radio
        var music = new ZoneMusic(ZoneCatalog.Load(), new SpotifyWebApi(Path.GetTempFileName()), () => cfg, () => GameOverrides.Empty);
        var (lua, _) = await ResolvedLua.BuildAsync(music);
        Assert.Contains("source = { default = \"radio\", spotify = false }", lua);
    }

    [Fact]
    public void WritesOnlyWhenPlaylistsChange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"resolved-{Guid.NewGuid():N}.lua");
        var empty = new Dictionary<string, ME>();
        var a = new Dictionary<string, ME> { ["map:1"] = new(new E("spotify:playlist:A", "A"), null) };
        try
        {
            Assert.True(ResolvedLua.WriteIfChanged(path, ResolvedLua.Render(a, empty, DateTime.Now)));
            Assert.False(ResolvedLua.WriteIfChanged(path, ResolvedLua.Render(a, empty, DateTime.Now.AddMinutes(5))));   // only the time differs
            a["map:1"] = new(new E("spotify:playlist:B", "B"), null);
            Assert.True(ResolvedLua.WriteIfChanged(path, ResolvedLua.Render(a, empty, DateTime.Now)));
        }
        finally { File.Delete(path); }
    }
}
