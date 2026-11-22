using AudioVibes.App;
using Xunit;

namespace AudioVibes.App.Tests;

public class SettingsFormTests
{
    private sealed class Host : ISettingsHost
    {
        public AppConfig Config { get; set; } = new();
        public bool StartsWithWindows { get; set; }
        public string SpotifyStatus => "Spotify: not set up (playing free radio)";
        public int Saved;
        public Task ConnectSpotifyAsync() => Task.CompletedTask;
        public Task DisconnectSpotifyAsync() => Task.CompletedTask;
        public void ConfigSaved() { Saved++; Config = AppConfig.Load()!; }
        public Updater Updater { get; } = new(() => true);
        public void ShowZonePlaylists() { }
        public IReadOnlyList<string> EventNames { get; } = ["EVT_LOGIN", "EVT_QUEUE_POP", "EVT_PLAYER_DEAD", "EVT_LEVEL_UP"];
    }

    private static void OnSta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        if (err != null) throw new Exception(err.Message, err);
    }

    private static T Find<T>(Control root, Func<T, bool> pick) where T : Control =>
        All(root).OfType<T>().First(pick);

    private static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => All(x).Prepend(x));

    [Fact]
    public void ShowsTheConfigAndSavesChanges()
    {
        var host = new Host();
        host.Config.ZonePlaylists["map:1429"] = "spotify:playlist:KEEP";   // not shown in this window: must survive
        host.Config.PlaylistSlots["20"] = "spotify:playlist:HIGHSLOT";
        OnSta(() =>
        {
            using var f = new SettingsForm(host) { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), ShowInTaskbar = false };
            f.Show();
            Application.DoEvents();

            var source = Find<ComboBox>(f, _ => true);
            Assert.Contains("Automatic", source.Text);
            source.SelectedIndex = 2;   // radio
            Find<CheckBox>(f, c => c.Text == "Start with Windows").Checked = true;
            Find<TextBox>(f, t => t.Width == 300).Text = "my-client-id";   // Spotify client ID
            Find<Button>(f, b => b.Text == "Apply").PerformClick();

            var shots = Environment.GetEnvironmentVariable("AUDIOVIBES_SCREENSHOTS");
            if (!string.IsNullOrEmpty(shots))
            {
                var tabs = Find<TabControl>(f, _ => true);
                for (int i = 0; i < tabs.TabCount; i++)
                {
                    tabs.SelectedIndex = i; Application.DoEvents();
                    using var bmp = new Bitmap(f.Width, f.Height);
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(Path.Combine(shots, $"settings-{i}-{tabs.TabPages[i].Text.Replace(' ', '-')}.png"));
                }
            }
            f.Close();
        });

        Assert.Equal(1, host.Saved);
        Assert.True(host.StartsWithWindows);
        var c = AppConfig.Load()!;
        Assert.Equal("radio", c.ZoneMusic.DefaultSource);
        Assert.Equal("my-client-id", c.Spotify.ClientId);
        Assert.True(c.Spotify.UseWebApi);                       // typing a client ID turns Spotify on
        Assert.Equal("spotify:playlist:KEEP", c.ZonePlaylists["map:1429"]);
        Assert.Equal("spotify:playlist:HIGHSLOT", c.PlaylistSlots["20"]);
        Assert.Equal("DUCK_ON", c.EventMap["EVT_QUEUE_POP"].Action);   // defaults kept
        Assert.Equal(8000, c.EventMap["EVT_QUEUE_POP"].AutoRevertMs);
        Assert.False(c.EventMap.ContainsKey("EVT_LOGIN"));              // unused events aren't written
        File.Delete(AppConfig.FilePath);
    }
}
