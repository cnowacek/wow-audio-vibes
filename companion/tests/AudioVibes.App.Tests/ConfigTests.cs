using AudioVibes.App;
using Xunit;

namespace AudioVibes.App.Tests;

public class ConfigTests
{
    [Fact]
    public void OldConfigsGetNewSettingsButKeepTheirValues()
    {
        // shaped like a config.json written before zone music / modes / updates existed
        File.WriteAllText(AppConfig.FilePath, """
        {
          // a comment people may have added
          "duckLevel": 0.15,
          "spotify": { "clientId": "abc", "useWebApi": true },
          "eventMap": { "EVT_QUEUE_POP": { "action": "NONE" } },
          "zonePlaylists": { "map:1429": "spotify:playlist:X" },
        }
        """);
        try
        {
            Assert.True(AppConfig.AddMissingSettings());
            var c = AppConfig.Load()!;
            Assert.Equal(0.15f, c.DuckLevel);
            Assert.Equal("abc", c.Spotify.ClientId);
            Assert.Equal("auto", c.ZoneMusic.DefaultSource);   // added
            Assert.True(c.AutoUpdate);                           // added
            Assert.Single(c.EventMap);                           // the user's own map isn't refilled
            Assert.Equal("NONE", c.EventMap["EVT_QUEUE_POP"].Action);
            Assert.Equal("spotify:playlist:X", c.ZonePlaylists["map:1429"]);
            var text = File.ReadAllText(AppConfig.FilePath);
            Assert.Contains("\"defaultSource\": \"auto\"", text);
            Assert.False(AppConfig.AddMissingSettings());       // nothing more to add: no rewrite
        }
        finally { File.Delete(AppConfig.FilePath); }
    }
}
