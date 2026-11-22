using AudioVibes.App;
using AudioVibes.Spotify;
using Xunit;

namespace AudioVibes.App.Tests;

public class ModeTests
{
    private const string PerModeSave = """
AudioVibesDB = {
	["locations"] = {
		["map:1454"] = {
			["relaxed"] = { ["uri"] = "spotify:playlist:SLOWTRIBAL", ["name"] = "Slow tribal", },
			["locked"] = { ["uri"] = "spotify:playlist:WARCAMP", ["name"] = "War camp (mine)", ["shuffle"] = true, },
		},
		["map:1429"] = {
			["relaxed"] = { ["uri"] = "spotify:playlist:GOLDSHIRE", },
		},
	},
	["types"] = {
		["raid"] = { ["locked"] = { ["uri"] = "spotify:album:RAIDMETAL", ["name"] = "Raid Night Metal", }, },
	},
	["modes"] = { ["map:1454"] = "relaxed", ["map:1"] = "sideways", },
	["settings"] = { ["resume"] = false, ["vibeMode"] = "auto", },
}
""";

    // These tests are about Spotify defaults; RadioDefaultTests covers the radio ones.
    private static ZoneMusic Music(AppConfig cfg, GameOverrides game)
    {
        cfg.ZoneMusic.DefaultSource = "spotify";
        return new(ZoneCatalog.Load(), new SpotifyWebApi(Path.GetTempFileName()), () => cfg, () => game);
    }

    [Fact]
    public void ParsesPerModeSaves()
    {
        var o = GameOverrides.FromLua(PerModeSave);
        Assert.Equal("spotify:playlist:SLOWTRIBAL", o.Locations["map:1454"].Relaxed!.Uri);
        Assert.True(o.Locations["map:1454"].Locked!.Shuffle);
        Assert.Null(o.Locations["map:1429"].Locked);
        Assert.Null(o.Types["raid"].Relaxed);
        Assert.Equal("relaxed", o.LocationModes["map:1454"]);
        Assert.False(o.LocationModes.ContainsKey("map:1"));            // junk value ignored
        Assert.False(o.Resume);
    }

    [Fact]
    public void AutoModeByType()
    {
        Assert.Equal(Modes.Relaxed, Modes.Auto("city"));
        Assert.Equal(Modes.Relaxed, Modes.Auto("zone"));
        Assert.Equal(Modes.Locked, Modes.Auto("dungeon"));
        Assert.Equal(Modes.Locked, Modes.Auto("raid"));
        Assert.Equal(Modes.Locked, Modes.Auto("pvp"));
    }

    [Fact]
    public async Task EachModeHasItsOwnPlaylist()
    {
        var music = Music(new AppConfig(), GameOverrides.FromLua(PerModeSave));
        var relaxed = await music.ChooseAsync("map:1454", null, resolveSearch: false, mode: Modes.Relaxed);
        var locked = await music.ChooseAsync("map:1454", null, resolveSearch: false, mode: Modes.Locked);
        Assert.Equal("spotify:playlist:SLOWTRIBAL", relaxed.Uri);
        Assert.Equal("spotify:playlist:WARCAMP", locked.Uri);
        Assert.True(locked.Shuffle);
        Assert.Equal(Modes.Locked, locked.Mode);

        // Elwynn: in-game Relaxed only; Locked-in falls through to the vibe default (not resolved offline)
        var elwynnLocked = await music.ChooseAsync("map:1429", null, resolveSearch: false, mode: Modes.Locked);
        Assert.Null(elwynnLocked.Uri);
        Assert.Equal("Open road", elwynnLocked.VibeName);
        Assert.Equal("Peaceful countryside", (await music.ChooseAsync("map:1429", null, false, mode: Modes.Relaxed)).VibeName);

        // a raid in Relaxed has no in-game "every raid" playlist for that mode
        Assert.Null((await music.ChooseAsync("instance:409", null, false, mode: Modes.Relaxed)).Uri);
        Assert.Equal("spotify:album:RAIDMETAL", (await music.ChooseAsync("instance:409", null, false, mode: Modes.Locked)).Uri);
    }

    [Fact]
    public async Task WithoutAModeSignalLocationsUseAutoOrTheirFixedMode()
    {
        var music = Music(new AppConfig(), GameOverrides.FromLua(PerModeSave));
        Assert.Equal(Modes.Locked, (await music.ChooseAsync("instance:36", null, false)).Mode);    // dungeon → Auto locked
        Assert.Equal(Modes.Relaxed, (await music.ChooseAsync("map:1429", null, false)).Mode);      // zone → Auto relaxed
        Assert.Equal(Modes.Relaxed, (await music.ChooseAsync("map:1454", null, false)).Mode);      // fixed in game
        // once the addon says the mode, that's what counts
        music.OnMode(Modes.Locked);
        Assert.Equal(Modes.Locked, (await music.ChooseAsync("map:1429", null, false)).Mode);
    }

    [Fact]
    public async Task CompanionOverridesPerModeAndForBoth()
    {
        var cfg = new AppConfig();
        cfg.ZonePlaylists["map:1453"] = "spotify:playlist:BOTH";
        cfg.ZonePlaylists["map:1453@locked"] = "spotify:playlist:SWLOCKED";
        cfg.VibePlaylists["spooky@relaxed"] = "off";
        var music = Music(cfg, GameOverrides.Empty);
        Assert.Equal("spotify:playlist:BOTH", (await music.ChooseAsync("map:1453", null, false, mode: Modes.Relaxed)).Uri);
        Assert.Equal("spotify:playlist:SWLOCKED", (await music.ChooseAsync("map:1453", null, false, mode: Modes.Locked)).Uri);
        var duskwood = await music.ChooseAsync("map:1431", null, false, mode: Modes.Relaxed);
        Assert.Null(duskwood.Uri);
        Assert.Contains("off", duskwood.Source);
    }

    [Fact]
    public void CatalogHasBothMoodsForEveryVibe()
    {
        foreach (var (key, v) in ZoneCatalog.Load().Vibes)
        {
            Assert.NotNull(v.Relaxed);
            Assert.False(string.IsNullOrWhiteSpace(v.For(Modes.Relaxed).Search), key);
            Assert.False(string.IsNullOrWhiteSpace(v.For(Modes.Locked).Search), key);
        }
        var cat = ZoneCatalog.Load();
        Assert.Equal("War camp", cat.VibeName("city_horde", Modes.Locked));
        Assert.Equal("Village fires", cat.VibeName("city_horde", Modes.Relaxed));
    }
}
