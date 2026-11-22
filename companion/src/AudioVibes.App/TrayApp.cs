using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using AudioVibes.Audio;
using AudioVibes.Decoder;
using AudioVibes.Spotify;
using Serilog;

namespace AudioVibes.App;

public sealed class TrayApp : ApplicationContext
{
    private enum Light { Grey, Yellow, Green, Red }

    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _status, _autostart, _spotifyStatus, _spotifyConnect, _spotifyDisconnect, _zoneStatus;
    private readonly ZoneMusic _zoneMusic;
    private readonly SavedVariablesWatcher _savedVars;
    private readonly System.Threading.Timer _resolvedTimer;

    private void ScheduleResolvedWrite() => _resolvedTimer.Change(2000, Timeout.Infinite);

    /// <summary>Writes the addon's Resolved.lua (chosen playlist + link per location) if anything changed.</summary>
    private async Task WriteResolvedAsync()
    {
        try
        {
            var client = _savedVars.ClientDir ?? GuessClientDir(null);
            if (client == null) return;
            var dir = Path.Combine(client, "Interface", "AddOns", "AudioVibes");
            if (!Directory.Exists(dir)) { Log.Information("addon folder not found at {Dir}; not writing Resolved.lua", dir); return; }
            var (lua, count) = await ResolvedLua.BuildAsync(_zoneMusic);
            var path = Path.Combine(dir, "Resolved.lua");
            if (ResolvedLua.WriteIfChanged(path, lua))
            {
                Log.Information("wrote {Path} ({Count} locations with a playlist link)", path, count);
                Post(() => _debug?.AddLog($"links for {count} locations sent to the addon (applies after /reload)"));
            }
        }
        catch (Exception e) { Log.Warning(e, "writing Resolved.lua failed"); }
    }

    /// <summary>WoW client folder holding WTF\: config override, else the running game's folder, else the default Forever install.</summary>
    private string? GuessClientDir(string? fromProcess)
    {
        if (!string.IsNullOrWhiteSpace(_config.WowClientFolder) && Directory.Exists(_config.WowClientFolder)) return _config.WowClientFolder;
        if (!string.IsNullOrEmpty(fromProcess) && Directory.Exists(Path.Combine(fromProcess, "WTF"))) return fromProcess;
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var forever = Path.Combine(pf, "World of Warcraft", "_forever_");
        return Directory.Exists(forever) ? forever : fromProcess;
    }
    private ZonePlaylistsForm? _zoneForm;
    private readonly Updater _updater;
    private readonly Dictionary<Light, Icon> _icons;
    private readonly ProtocolSpec _protocol;
    private readonly FrameDecoder _decoder;
    private readonly WowAudioSource _source;
    private readonly ActionRouter _router;
    private readonly SpotifyTransport _transport = new();
    private readonly SpotifyWebApi _web;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Windows.Forms.Timer _tooltipTimer;
    private AppConfig _config;
    private DebugForm? _debug;
    private bool _pinged;
    private string _nowPlaying = "";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        try { MigrateAutostart(); } catch (Exception e) { Log.Warning(e, "autostart migration failed"); }
        _config = AppConfig.LoadOrCreate();
        _protocol = ProtocolSpec.LoadDefault();
        ApplyDetection();

        _icons = new() { [Light.Grey] = Dot(Color.Gray), [Light.Yellow] = Dot(Color.Gold), [Light.Green] = Dot(Color.LimeGreen), [Light.Red] = Dot(Color.Red) };

        _status = new ToolStripMenuItem("Status: starting") { Enabled = false };
        _autostart = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart()) { Checked = IsAutostart() };
        _spotifyStatus = new ToolStripMenuItem("Spotify: checking…") { Enabled = false };
        _spotifyConnect = new ToolStripMenuItem("Connect Spotify (Web API)", null, async (_, _) => await ConnectSpotifyAsync());
        _spotifyDisconnect = new ToolStripMenuItem("Disconnect Spotify", null, async (_, _) => await DisconnectSpotifyAsync()) { Visible = false };
        var menu = new ContextMenuStrip();
        _zoneStatus = new ToolStripMenuItem("Zone: none yet") { Enabled = false };
        menu.Items.Add(_status);
        menu.Items.Add(_spotifyStatus);
        menu.Items.Add(_zoneStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Zone playlists…", null, (_, _) => ShowZonePlaylists());
        menu.Items.Add("Open debug window", null, (_, _) => ShowDebug());
        menu.Items.Add("Open config", null, (_, _) => Shell(AppConfig.FilePath));
        menu.Items.Add("Open logs folder", null, (_, _) => Shell(Path.Combine(AppConfig.Dir, "logs")));
        menu.Items.Add(_spotifyConnect);
        menu.Items.Add(_spotifyDisconnect);
        menu.Items.Add(_autostart);
        menu.Items.Add(new ToolStripSeparator());
        _updater = new Updater(() => _config.AutoUpdate);
        var restartToUpdate = new ToolStripMenuItem("Restart to update", null, (_, _) => _updater.RestartNow()) { Visible = false };
        menu.Items.Add(new ToolStripMenuItem("Version " + _updater.VersionText) { Enabled = false });
        menu.Items.Add("Check for updates", null, async (_, _) => { var m = await _updater.CheckAsync(true); if (m != "") Balloon(m, false); });
        menu.Items.Add(restartToUpdate);
        _updater.ReadyChanged += v => Post(() =>
        {
            restartToUpdate.Text = $"Restart to update to {v}";
            restartToUpdate.Visible = true;
            Balloon($"Update {v} downloaded. It installs when you quit, or choose Restart to update.", false);
        });
        _updater.Start();
        menu.Items.Add("Quit", null, (_, _) => { _updater.ApplyOnExit(); ExitThread(); });

        _tray = new NotifyIcon { Icon = _icons[Light.Grey], Visible = true, ContextMenuStrip = menu, Text = AppName };
        _tray.DoubleClick += (_, _) => ShowDebug();

        _web = new SpotifyWebApi(Path.Combine(AppConfig.Dir, "spotify.token"));
        ApplySpotify();
        var volume = new SpotifyVolume();
        _resolvedTimer = new System.Threading.Timer(_ => _ = WriteResolvedAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _savedVars = new SavedVariablesWatcher();
        _savedVars.Changed += o =>
        {
            Post(() => _debug?.AddLog(
                $"in-game playlists loaded: {o.Locations.Count} locations, {o.Types.Count} types{(o.Hype != null ? ", hype" : "")}"));
            ScheduleResolvedWrite();   // names learned in game may add locations
        };
        _zoneMusic = new ZoneMusic(ZoneCatalog.Load(), _web, () => _config, () => _savedVars.Current);
        _zoneMusic.DefaultsChanged += ScheduleResolvedWrite;
        _savedVars.SetClientDir(GuessClientDir(null));
        ScheduleResolvedWrite();
        _zoneMusic.Notify += (msg, err) => Post(() => Balloon(msg, err));
        _zoneMusic.Changed += c => Post(() =>
        {
            var t = $"Zone: {c.ZoneName} → {c.PlaylistLabel}";
            _zoneStatus.Text = (t.Length > 90 ? t[..90] + "…" : t).Replace("&", "&&");
            _debug?.AddLog($"♪ {c.ZoneName} [{c.ZoneKey}] → {c.VibeName} → {c.PlaylistLabel} ({c.Source})");
        });
        _router = new ActionRouter(_transport, volume, _web, _zoneMusic, () => _config);
        _router.PingReceived += () => Post(() => { _pinged = true; UpdateState(); Balloon("WoW addon connected", false); });
        _router.Notify += (msg, err) => Post(() => Balloon(msg, err));

        _decoder = new FrameDecoder(_protocol);
        _decoder.Decoded += cmd =>
        {
            Log.Information("decoded {Name} ({Code}) margin {Margin:F1} dB", cmd.Name, cmd.Code, cmd.ConfidenceDb);
            Post(() => _debug?.AddLog($"✔ {cmd.Name} ({cmd.Code})  margin {cmd.ConfidenceDb:F1} dB"));
            _ = _router.HandleAsync(cmd);
        };
        _decoder.ZoneDecoded += z =>
        {
            Log.Information("decoded zone {Key} margin {Margin:F1} dB", z.Key, z.ConfidenceDb);
            Post(() => _debug?.AddLog($"✔ ZONE {z.Key}  margin {z.ConfidenceDb:F1} dB"));
            if (_config.ZoneMusic.Enabled) _zoneMusic.OnZone(z);
        };
        _decoder.Rejected += r =>
        {
            Log.Debug("rejected frame: {Reason}", r.Reason);
            Post(() => _debug?.AddLog($"✘ rejected: {r.Reason}"));
        };
        _decoder.Levels += l => _debug?.PushLevels(l);

        _source = new WowAudioSource(_config.WowProcessNames, _protocol.SampleRate);
        _source.SamplesAvailable += block => _decoder.Feed(block);
        _source.StateChanged += (s, msg) =>
        {
            Log.Information("audio source: {State} {Message}", s, msg);
            if (s != SourceState.Attached) _pinged = false;
            if (s == SourceState.Attached)
            {
                _decoder.Reset();
                _savedVars.SetClientDir(GuessClientDir(_source.ClientDir));
            }
            Post(UpdateState);
        };
        _source.RecordingFinished += path => Post(() =>
        {
            _debug?.AddLog($"● recording saved: {path}");
            Balloon("Recording saved: " + Path.GetFileName(path), false);
        });

        // Editors save differently: in place (Changed) or via temp file + swap (Created/Renamed, e.g. Windows 11
        // Notepad, VS Code). Watch the whole folder and react to any event that ends at config.json.
        _watcher = new FileSystemWatcher(AppConfig.Dir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        FileSystemEventHandler onFs = (_, e) => { if (IsConfig(e.FullPath)) Post(() => ReloadConfig()); };
        _watcher.Changed += onFs;
        _watcher.Created += onFs;
        _watcher.Renamed += (_, e) => { if (IsConfig(e.FullPath)) Post(() => ReloadConfig()); };
        _watcher.EnableRaisingEvents = true;

        _tooltipTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _tooltipTimer.Tick += async (_, _) => { _nowPlaying = await _transport.NowPlayingAsync() ?? ""; UpdateState(); };
        _tooltipTimer.Start();

        if (!ProcessLoopbackCapture.IsSupported) Balloon(ProcessLoopbackCapture.SupportMessage, true);
        _source.Start();
        UpdateState();
        _ = RefreshSpotifyStatusAsync();
        Log.Information("WoW Audio Vibes started; config {Path}", AppConfig.FilePath);
    }

    private void Post(Action a) => _ui.Post(_ => a(), null);

    private void ApplyDetection()
    {
        _protocol.MinPreambleDb = _config.Detection.MinPreambleDb;
        _protocol.MinSymbolMarginDb = _config.Detection.MinSymbolMarginDb;
        _protocol.DebounceMs = _config.Detection.DebounceMs;
    }

    private void ApplySpotify()
    {
        _web.ClientId = _config.Spotify.ClientId;
        _web.Enabled = _config.Spotify.UseWebApi;
    }

    private static bool IsConfig(string path) =>
        string.Equals(Path.GetFileName(path), "config.json", StringComparison.OrdinalIgnoreCase);

    private DateTime _lastReload;
    private void ReloadConfig(bool force = false)
    {
        if (!force && (DateTime.Now - _lastReload).TotalMilliseconds < 300) return; // watcher fires several times per save
        _lastReload = DateTime.Now;
        try
        {
            var fresh = AppConfig.Load();
            if (fresh is null) { Log.Warning("config.json missing during reload; keeping current settings"); return; }
            bool spotifyChanged = fresh.Spotify.ClientId != _config.Spotify.ClientId || fresh.Spotify.UseWebApi != _config.Spotify.UseWebApi;
            _config = fresh;
            ApplyDetection();
            ApplySpotify();
            if (spotifyChanged) _ = RefreshSpotifyStatusAsync();
            _source.SetProcessNames(_config.WowProcessNames);
            Log.Information("config reloaded");
            ScheduleResolvedWrite();   // zone/vibe playlists set in the companion window
            _debug?.AddLog("config reloaded");
        }
        catch (Exception e)
        {
            Log.Warning(e, "config reload failed");
            Balloon("config.json has an error: " + e.Message, true);
        }
    }

    private void UpdateState()
    {
        Light light = _source.State switch
        {
            SourceState.Error => Light.Red,
            SourceState.Attached => _pinged ? Light.Green : Light.Yellow,
            _ => Light.Grey,
        };
        string text = light switch
        {
            Light.Green => "connected (addon PING received)",
            Light.Yellow => "attached to WoW, waiting for addon PING (/vibes test)",
            Light.Red => _source.Status,
            _ => "WoW not running",
        };
        _tray.Icon = _icons[light];
        _status.Text = "Status: " + text;
        var tip = AppName + " – " + text + (_nowPlaying.Length > 0 ? "\n♪ " + _nowPlaying : "");
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
        _debug?.SetStatus(_source.Status);
    }

    private void Balloon(string msg, bool error)
    {
        _tray.ShowBalloonTip(3000, AppName, msg, error ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void ShowDebug()
    {
        if (_debug == null || _debug.IsDisposed)
        {
            _debug = new DebugForm(_protocol, StartRecording);
            _debug.SetStatus(_source.Status);
        }
        _debug.Show();
        _debug.Activate();
    }

    private void ShowZonePlaylists()
    {
        if (_zoneForm == null || _zoneForm.IsDisposed) _zoneForm = new ZonePlaylistsForm(_zoneMusic, () => _config);
        _zoneForm.Show();
        _zoneForm.Activate();
    }

    private void StartRecording()
    {
        if (_source.State != SourceState.Attached) { Balloon("WoW isn't attached; can't record", true); return; }
        var dir = Path.Combine(AppConfig.Dir, "recordings");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"wow-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        _source.Record(path, TimeSpan.FromSeconds(60));
        _debug?.AddLog($"● recording 60 s → {path}");
    }

    private async Task ConnectSpotifyAsync()
    {
        ReloadConfig(force: true); // don't depend on the file watcher having caught the last save
        if (string.IsNullOrWhiteSpace(_config.Spotify.ClientId))
        {
            Balloon("Set spotify.clientId in config.json first (see README)", true);
            Shell(AppConfig.FilePath);
            return;
        }
        try
        {
            _web.ClientId = _config.Spotify.ClientId;
            await _web.AuthorizeAsync(url => Shell(url));
            var who = await RefreshSpotifyStatusAsync();
            Balloon(!_config.Spotify.UseWebApi ? "Spotify connected. Set spotify.useWebApi = true in config.json to use it."
                    : who != null ? $"Spotify connected as {who}" : "Spotify connected", false);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Spotify auth failed");
            Balloon("Spotify connect failed: " + e.Message, true);
        }
    }

    private async Task DisconnectSpotifyAsync()
    {
        _web.Disconnect();
        Log.Information("Spotify login removed");
        await RefreshSpotifyStatusAsync();
        Balloon("Spotify disconnected on this PC. To revoke fully, remove the app at spotify.com/account/apps.", false);
    }

    /// <summary>Updates the Spotify lines in the tray menu. Returns the display name when connected.</summary>
    private async Task<string?> RefreshSpotifyStatusAsync()
    {
        string status; bool connected = false; string? who = null;
        if (string.IsNullOrWhiteSpace(_config.Spotify.ClientId))
            status = "Spotify: Web API not set up";
        else if (!_web.HasRefreshToken)
            status = "Spotify: not connected";
        else if (!_config.Spotify.UseWebApi)
        {
            status = "Spotify: signed in, but useWebApi is off in config";
            connected = true;
        }
        else
        {
            try
            {
                var me = await _web.GetProfileAsync();
                who = me.DisplayName;
                status = $"Spotify: connected as {who}";
                // Look up any default playlists not found yet, so every location gets a link in the addon.
                _ = Task.Run(async () =>
                {
                    try { await _zoneMusic.ResolveMissingDefaultsAsync(); }
                    catch (Exception e) { Log.Warning(e, "looking up default playlists failed"); }
                    ScheduleResolvedWrite();
                });
                connected = true;
            }
            catch (HttpRequestException e) when (e.Message.Contains("400") || e.Message.Contains("401"))
            {
                Log.Warning(e, "Spotify login rejected");
                status = "Spotify: login expired, reconnect needed";
            }
            catch (Exception e)
            {
                Log.Warning(e, "Spotify profile lookup failed");
                status = "Spotify: signed in (couldn't reach Spotify)";
                connected = true;
            }
        }
        Post(() =>
        {
            _spotifyStatus.Text = status.Replace("&", "&&"); // '&' would become a menu mnemonic
            _spotifyConnect.Text = connected ? "Reconnect Spotify…" : "Connect Spotify (Web API)";
            _spotifyDisconnect.Visible = _web.HasRefreshToken;
        });
        return who;
    }

    public const string AppName = "WoW Audio Vibes";
    private const string LegacyRunValue = "Murmur";   // name used before the rename

    private static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(AppName) is string;
    }

    /// <summary>Carries "Start with Windows" over from the old name, pointing at this exe.</summary>
    private static void MigrateAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        // The installed app takes over a Start-with-Windows entry left by a dev build (or an old install path).
        var self = $"\"{Environment.ProcessPath}\"";
        if (k?.GetValue(AppName) is string cur && cur != self
            && (Environment.ProcessPath ?? "").Contains(@"\WoWAudioVibes\current\", StringComparison.OrdinalIgnoreCase))
        {
            k.SetValue(AppName, self);
            Log.Information("Start with Windows now points at {Exe}", self);
        }
        if (k?.GetValue(LegacyRunValue) is not string) return;
        k.DeleteValue(LegacyRunValue, false);
        k.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        Log.Information("moved Start with Windows entry from {Old} to {New}", LegacyRunValue, AppName);
    }

    /// <summary>Uninstall hook: don't leave a Start-with-Windows entry pointing at a deleted exe.</summary>
    public static void RemoveAutostart()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            k?.DeleteValue(AppName, false);
        }
        catch { /* uninstall must not fail over this */ }
    }

    private void ToggleAutostart()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsAutostart()) k.DeleteValue(AppName, false);
        else k.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        _autostart.Checked = IsAutostart();
    }

    private static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { Log.Warning(e, "could not open {Target}", target); }
    }

    private static Icon Dot(Color c)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(c);
            g.FillEllipse(b, 3, 3, 26, 26);
            using var p = new Pen(Color.FromArgb(160, 0, 0, 0), 2);
            g.DrawEllipse(p, 3, 3, 26, 26);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _source.Dispose();
        _watcher.Dispose();
        _savedVars.Dispose();
        _resolvedTimer.Dispose();
        _tooltipTimer.Dispose();
        _debug?.Close();
        Log.CloseAndFlush();
        base.ExitThreadCore();
    }
}
