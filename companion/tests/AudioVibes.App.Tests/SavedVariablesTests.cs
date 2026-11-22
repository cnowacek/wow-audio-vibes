using AudioVibes.App;
using AudioVibes.Spotify;
using Xunit;

namespace AudioVibes.App.Tests;

public class SavedVariablesTests
{
    // Shaped like a real WTF\Account\X\SavedVariables\AudioVibes.lua written by WoW.
    private const string Sample = """

AudioVibesDB = {
	["debug"] = false,
	["locations"] = {
		["instance:36"] = {
			["shuffle"] = true,
			["name"] = "My Pirate Mix",
			["uri"] = "spotify:playlist:5xP1r4teMix0Defias",
		},
		["map:1429"] = {
			["name"] = "Goldshire \"Hearth\"",
			["uri"] = "https://open.spotify.com/playlist/ABC123?si=xyz",
			["shuffle"] = false,
		},
		["map:9"] = {
			["name"] = "broken",
			["uri"] = "https://youtube.com/watch?v=no",
		},
	},
	["types"] = {
		["raid"] = {
			["name"] = "Raid Night Metal",
			["uri"] = "spotify:album:2noRn2Aes5aoNVsU6iWThc",
		},
	},
	["hype"] = {
		["name"] = "Hype!",
		["uri"] = "spotify:playlist:HYPE",
	},
	["seen"] = {
		["map:2600"] = {
			["type"] = "zone",
			["name"] = "Zephras Isle",
		},
		["instance:2001"] = {
			["type"] = "dungeon",
			["name"] = "Hall of Th\195\162nes",
		},
	},
	["settings"] = {
		["shuffle"] = true,
		["autoSwitch"] = true,
	},
	["pending"] = {
	},
	["bar"] = {
		["point"] = {
			"CENTER", -- [1]
			"CENTER", -- [2]
			12.5, -- [3]
			-220, -- [4]
		},
	},
}
OtherAddonDB = nil
""";

    [Fact]
    public void ParsesWowSavedVariables()
    {
        var g = LuaSavedVariables.Parse(Sample);
        var db = Assert.IsType<LuaSavedVariables.LuaTable>(g["AudioVibesDB"]);
        Assert.Equal(false, db["debug"]);
        var point = db.Table("bar")!.Table("point")!;
        Assert.Equal("CENTER", point[1.0]);
        Assert.Equal(12.5, point[3.0]);
        Assert.Equal(-220.0, point[4.0]);
        Assert.Null(g["OtherAddonDB"]);
    }

    [Fact]
    public void ExtractsOverrides()
    {
        var o = GameOverrides.FromLua(Sample);
        Assert.Equal(2, o.Locations.Count);                          // the YouTube link is dropped
        // this sample is the pre-modes format: one playlist counts for both modes
        var deadmines = new GamePlaylist("spotify:playlist:5xP1r4teMix0Defias", "My Pirate Mix", true);
        Assert.Equal(deadmines, o.Locations["instance:36"].Relaxed);
        Assert.Equal(deadmines, o.Locations["instance:36"].Locked);
        Assert.Equal("spotify:playlist:ABC123", o.Locations["map:1429"].For(Modes.Relaxed)!.Uri);
        Assert.Equal("Goldshire \"Hearth\"", o.Locations["map:1429"].For(Modes.Locked)!.Name);
        Assert.Equal("spotify:album:2noRn2Aes5aoNVsU6iWThc", o.Types["raid"].For(Modes.Locked)!.Uri);
        Assert.True(o.Resume);   // default when the setting isn't saved
        Assert.Equal("spotify:playlist:HYPE", o.Hype!.Uri);
        Assert.True(o.Shuffle);
        Assert.Equal(("Zephras Isle", "zone"), o.Seen["map:2600"]);
        Assert.Equal("Hall of Thânes", o.Seen["instance:2001"].Name);   // \195\162 = UTF-8 'â'
    }

    [Fact]
    public void EmptyOrMissingIsHarmless()
    {
        Assert.Empty(GameOverrides.FromLua("").Locations);
        Assert.Empty(GameOverrides.FromLua("AudioVibesDB = {}").Locations);
        Assert.Throws<FormatException>(() => LuaSavedVariables.Parse("AudioVibesDB = { [\"x\" = 1 }"));
    }

    [Fact]
    public async Task InGamePlaylistsWinInTheRightOrder()
    {
        var cfg = new AppConfig();
        var game = GameOverrides.FromLua(Sample);
        var music = new ZoneMusic(ZoneCatalog.Load(), new SpotifyWebApi(Path.GetTempFileName()), () => cfg, () => game);

        // in-game location playlist beats the companion's zone playlist
        cfg.ZonePlaylists["instance:36"] = "spotify:playlist:COMPANION";
        var dm = await music.ChooseAsync("instance:36", null, false);
        Assert.Equal("spotify:playlist:5xP1r4teMix0Defias", dm.Uri);
        Assert.True(dm.Shuffle);

        // companion zone playlist beats the in-game "every raid" playlist
        cfg.ZonePlaylists["instance:409"] = "spotify:playlist:MC";
        Assert.Equal("spotify:playlist:MC", (await music.ChooseAsync("instance:409", null, false)).Uri);
        // other raids get the in-game type playlist
        Assert.Equal("spotify:album:2noRn2Aes5aoNVsU6iWThc", (await music.ChooseAsync("instance:469", null, false)).Uri);

        // names learned in game label unknown locations
        Assert.Equal("Zephras Isle", music.NameOf("map:2600"));
        Assert.Equal("dungeon", music.TypeOf("instance:2001"));
    }
}
