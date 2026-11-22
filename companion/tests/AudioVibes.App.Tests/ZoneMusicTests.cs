using AudioVibes.App;
using AudioVibes.Spotify;
using Xunit;

namespace AudioVibes.App.Tests;

public class ZoneMusicTests
{
    private static readonly ZoneCatalog Catalog = ZoneCatalog.Load();

    [Fact]
    public void CatalogIsConsistent()
    {
        Assert.True(Catalog.Vibes.Count >= 10);
        Assert.Contains(ZoneCatalog.FallbackVibe, Catalog.Vibes.Keys);
        foreach (var (key, z) in Catalog.Zones)
        {
            Assert.Matches(@"^(map|instance):\d+$", key);
            Assert.True(Catalog.Vibes.ContainsKey(z.Vibe), $"{key} uses unknown vibe {z.Vibe}");
            Assert.Contains(z.Type, new[] { "city", "zone", "dungeon", "raid", "pvp" });
        }
        foreach (var (evt, vibe) in Catalog.Categories)
        {
            Assert.StartsWith("EVT_ENTER_", evt);
            Assert.True(Catalog.Vibes.ContainsKey(vibe), $"{evt} → unknown vibe {vibe}");
        }
        foreach (var v in Catalog.Vibes.Values) Assert.False(string.IsNullOrWhiteSpace(v.Search) && v.Uri == null);
    }

    [Theory]
    [InlineData("map:1429", null, "pastoral")]
    [InlineData("map:1431", null, "spooky")]
    [InlineData("instance:36", "EVT_ENTER_DUNGEON", "dungeon")]
    [InlineData("instance:9999", "EVT_ENTER_RAID", "raid")]        // unknown Forever raid → category
    [InlineData("map:9999", null, "adventure")]                    // unknown zone, no category → fallback
    [InlineData(null, "EVT_ENTER_PVP", "battle")]
    public void VibeResolution(string? key, string? category, string expected) =>
        Assert.Equal(expected, Catalog.VibeFor(key, category));

    [Theory]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=abc", "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/intl-de/playlist/abc123", "spotify:playlist:abc123")]
    [InlineData("https://open.spotify.com/album/xyz", "spotify:album:xyz")]
    [InlineData("spotify:playlist:abc", "spotify:playlist:abc")]
    [InlineData("  ", null)]
    [InlineData("https://example.com/playlist/abc", null)]
    [InlineData("hello", null)]
    public void SpotifyLinksNormalize(string input, string? expected) => Assert.Equal(expected, SpotifyUri.Normalize(input));

    [Fact]
    public async Task OverridesWinInOrder()
    {
        var cfg = new AppConfig();
        var music = new ZoneMusic(Catalog, new SpotifyWebApi(Path.GetTempFileName()), () => cfg);

        // nothing set, no network: default is the vibe's search (not resolved here)
        var c0 = await music.ChooseAsync("map:1429", null, resolveSearch: false);
        Assert.Equal("pastoral", c0.Vibe);
        Assert.Equal("Elwynn Forest", c0.ZoneName);

        // vibe override applies to every zone of that vibe
        cfg.VibePlaylists["pastoral"] = "https://open.spotify.com/playlist/VIBE?si=1";
        Assert.Equal("spotify:playlist:VIBE", (await music.ChooseAsync("map:1436", null, false)).Uri);   // Westfall

        // zone override beats vibe override, for that zone only
        cfg.ZonePlaylists["map:1429"] = "spotify:playlist:ELWYNN";
        Assert.Equal("spotify:playlist:ELWYNN", (await music.ChooseAsync("map:1429", null, false)).Uri);
        Assert.Equal("spotify:playlist:VIBE", (await music.ChooseAsync("map:1436", null, false)).Uri);

        // "off" means leave the music alone in that zone
        cfg.ZonePlaylists["map:1431"] = "off";
        var off = await music.ChooseAsync("map:1431", null, false);
        Assert.Null(off.Uri);
        Assert.Contains("off", off.Source);
    }
}
