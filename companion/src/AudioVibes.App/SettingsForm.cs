using System.Diagnostics;
using System.Text.Json;

namespace AudioVibes.App;

/// <summary>What the Settings window needs from the tray app.</summary>
public interface ISettingsHost
{
    AppConfig Config { get; }
    bool StartsWithWindows { get; set; }
    string SpotifyStatus { get; }
    Task ConnectSpotifyAsync();
    Task DisconnectSpotifyAsync();
    /// <summary>Called after config.json was saved: apply it now (don't wait for the file watcher).</summary>
    void ConfigSaved();
    Updater Updater { get; }
    void ShowZonePlaylists();
    IReadOnlyList<string> EventNames { get; }
}

/// <summary>
/// Every companion setting in one window, so nobody has to edit config.json. Saving writes config.json
/// (still there for advanced use) and applies it straight away.
/// </summary>
public sealed class SettingsForm : Form
{
    public const string RedirectUri = "http://127.0.0.1:8888/callback";

    private readonly ISettingsHost _host;
    private AppConfig _cfg;

    // General
    private readonly CheckBox _autostart = Check("Start with Windows");
    private readonly CheckBox _autoUpdate = Check("Install updates automatically");
    private readonly TextBox _wowFolder = new() { Width = 360 };
    // Music
    private readonly CheckBox _zoneMusic = Check("Change the music when I change location");
    private readonly ComboBox _defaultSource = Combo(
        ("auto", "Automatic: Spotify when connected, otherwise free radio"),
        ("spotify", "Spotify playlists"),
        ("radio", "Free internet radio stations"),
        ("off", "Nothing (only playlists I've chosen)"));
    private readonly NumericUpDown _fanfare = Num(5, 600, 1);
    private readonly NumericUpDown _volumeStep = Num(1, 50, 1);
    private readonly NumericUpDown _duck = Num(0, 100, 5);
    // Spotify
    private readonly Label _spotifyStatus = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
    private readonly TextBox _clientId = new() { Width = 300 };
    private readonly CheckBox _useSpotify = Check("Use Spotify (playlists, Like, Spotify defaults)");
    private readonly Button _connect = new() { Text = "Connect Spotify…", AutoSize = true };
    private readonly Button _disconnect = new() { Text = "Disconnect", AutoSize = true };
    // Events
    private readonly DataGridView _events = Grid();
    private readonly DataGridView _slots = Grid();
    // Advanced
    private readonly NumericUpDown _preamble = Num(3, 40, 0.5m, 1);
    private readonly NumericUpDown _margin = Num(1, 30, 0.5m, 1);
    private readonly NumericUpDown _debounce = Num(0, 5000, 50);
    private readonly NumericUpDown _decisionDelay = Num(0, 5000, 100);
    private readonly TextBox _processNames = new() { Width = 360 };

    private static readonly (string Value, string Label)[] Actions =
    [
        ("NONE", "Do nothing"), ("PLAY_PAUSE", "Play / pause"), ("NEXT", "Next track"), ("PREV", "Previous track"),
        ("VOL_UP", "Volume up"), ("VOL_DOWN", "Volume down"), ("DUCK_ON", "Lower the music"), ("DUCK_OFF", "Restore the volume"),
        ("LIKE_CURRENT", "Like the track (Spotify)"), ("PLAYLIST_SLOT", "Play a playlist slot"), ("FANFARE", "Level-up fanfare"),
    ];

    private static readonly Dictionary<string, string> EventLabels = new()
    {
        ["EVT_LOGIN"] = "Logged in", ["EVT_QUEUE_POP"] = "Queue popped", ["EVT_PLAYER_DEAD"] = "You died",
        ["EVT_PLAYER_ALIVE"] = "You came back to life", ["EVT_COMBAT_START"] = "Entered combat", ["EVT_COMBAT_END"] = "Left combat",
        ["EVT_LEVEL_UP"] = "Level up", ["EVT_HYPE"] = "Hype button (when no Hype playlist is set in game)",
        ["EVT_ENTER_CITY"] = "Entered a city *", ["EVT_ENTER_OPEN_WORLD"] = "Entered the open world *",
        ["EVT_ENTER_DUNGEON"] = "Entered a dungeon *", ["EVT_ENTER_RAID"] = "Entered a raid *", ["EVT_ENTER_PVP"] = "Entered PvP *",
    };

    public SettingsForm(ISettingsHost host)
    {
        _host = host;
        _cfg = Clone(host.Config);
        Text = "WoW Audio Vibes – Settings";
        Font = new Font("Segoe UI", 9);
        Width = 720; Height = 600;
        MinimumSize = new Size(640, 520);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("General", GeneralPage()));
        tabs.TabPages.Add(Page("WoW addon", AddonPage()));
        tabs.TabPages.Add(Page("Music", MusicPage()));
        tabs.TabPages.Add(Page("Spotify", SpotifyPage()));
        tabs.TabPages.Add(Page("Game events", EventsPage()));
        tabs.TabPages.Add(Page("Advanced", AdvancedPage()));

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "Save", AutoSize = true };
        save.Click += (_, _) => { if (SaveSettings()) Close(); };
        var apply = new Button { Text = "Apply", AutoSize = true };
        apply.Click += (_, _) => SaveSettings();
        buttons.Controls.AddRange([cancel, save, apply]);
        AcceptButton = save; CancelButton = cancel;

        Controls.Add(tabs);
        Controls.Add(buttons);
        Fill();
        Activated += (_, _) => RefreshSpotify();
    }

    // ------------------------------------------------------------------ pages

    private Control GeneralPage()
    {
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "WoW client folder (the one with WTF and Interface in it, e.g. ...\\World of Warcraft\\_forever_)", UseDescriptionForTitle = true };
            if (Directory.Exists(_wowFolder.Text)) d.InitialDirectory = _wowFolder.Text;
            if (d.ShowDialog(this) == DialogResult.OK) _wowFolder.Text = d.SelectedPath;
        };
        var check = new Button { Text = "Check for updates now", AutoSize = true, Enabled = _host.Updater.IsInstalled };
        var updateMsg = Hint("");
        check.Click += async (_, _) => { check.Enabled = false; updateMsg.Text = await _host.Updater.CheckAsync(true); check.Enabled = true; };

        return Stack(
            _autostart,
            Hint("Starts the companion (in the tray) when you sign in to Windows."),
            Gap(),
            _autoUpdate,
            Row(new Label { Text = "Version " + _host.Updater.VersionText, AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, check),
            updateMsg,
            Hint(_host.Updater.IsInstalled ? "New versions download in the background and install when you quit the companion."
                                           : "Updates only work in the installed app (Setup.exe from GitHub Releases)."),
            Gap(),
            Bold("WoW client folder"),
            Row(_wowFolder, browse),
            Hint("Leave empty to find it automatically from the running game. It's the folder with WTF and Interface in it."));
    }

    private readonly DataGridView _clients = Grid();
    private readonly CheckBox _keepAddon = Check("Keep the addon up to date when the companion updates");
    private readonly Label _addonMsg = Hint("");

    private Control AddonPage()
    {
        _clients.Dock = DockStyle.None;
        _clients.Size = new Size(650, 170);
        _clients.ReadOnly = true;
        _clients.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _clients.Columns.Add(new DataGridViewTextBoxColumn { Name = "client", HeaderText = "WoW", FillWeight = 70 });
        _clients.Columns.Add(new DataGridViewTextBoxColumn { Name = "status", HeaderText = "Audio Vibes addon", FillWeight = 120 });
        _clients.Columns.Add(new DataGridViewTextBoxColumn { Name = "path", HeaderText = "Folder", FillWeight = 180 });
        _clients.Columns.Add(new DataGridViewButtonColumn { Name = "action", HeaderText = "", FillWeight = 55, FlatStyle = FlatStyle.System });
        _clients.CellContentClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || _clients.Columns[e.ColumnIndex].Name != "action") return;
            if (_clients.Rows[e.RowIndex].Tag is AddonStatus s) await InstallAsync(s);
        };

        var add = new Button { Text = "Add a WoW folder…", AutoSize = true };
        add.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Your WoW client folder, e.g. ...\\World of Warcraft\\_forever_", UseDescriptionForTitle = true };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            _wowFolder.Text = d.SelectedPath;   // saved as the WoW client folder (General tab)
            RefreshClients();
        };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => RefreshClients();
        var openAddons = new Button { Text = "Open AddOns folder", AutoSize = true };
        openAddons.Click += (_, _) => { if (_clients.CurrentRow?.Tag is AddonStatus s) Open(s.Client.AddOnsDir); };

        return Stack(
            Bold("Install the addon into WoW"),
            Hint(AddonInstaller.HasBundle
                ? $"The companion comes with the Audio Vibes addon (version {AddonInstaller.BundledVersion ?? "?"}). Install it into each WoW you play:"
                : "This build doesn't include the addon files (it's a development build); use scripts\\link-addon.ps1 instead."),
            _clients,
            Row(add, refresh, openAddons),
            _addonMsg,
            Gap(),
            _keepAddon,
            Hint("Only updates WoW folders where the addon is already installed. Folders linked to a development copy are never touched."),
            Gap(),
            Hint("After installing or updating, restart WoW completely (exit to desktop): /reload doesn't load new sound files."));
    }

    private void RefreshClients()
    {
        _clients.Rows.Clear();
        var extra = new[] { _wowFolder.Text.Trim(), _cfg.WowClientFolder };
        var clients = AddonInstaller.FindClients(extra);
        if (clients.Count == 0) { _addonMsg.Text = "No WoW found. Use \"Add a WoW folder…\" and pick the folder with Interface and WTF in it."; return; }
        foreach (var c in clients)
        {
            var s = AddonInstaller.Status(c);
            var (text, action) = s.State switch
            {
                AddonState.NotInstalled => ("Not installed", "Install"),
                AddonState.UpToDate => ($"Installed, up to date ({s.InstalledVersion})", "Reinstall"),
                AddonState.Outdated => ($"Version {s.InstalledVersion ?? "?"}: update available", "Update"),
                AddonState.Different => ($"Installed ({s.InstalledVersion ?? "?"}), different files", "Replace"),
                AddonState.Linked => ("Linked to a development folder", ""),
                _ => ("", ""),
            };
            int i = _clients.Rows.Add(c.Label, text, c.Path, action);
            _clients.Rows[i].Tag = s;
            if (action == "" || !AddonInstaller.HasBundle) _clients.Rows[i].Cells["action"] = new DataGridViewTextBoxCell { Value = "" };
        }
        if (_addonMsg.Text.StartsWith("No WoW")) _addonMsg.Text = "";
    }

    private async Task InstallAsync(AddonStatus s)
    {
        if (s.State == AddonState.Different && MessageBox.Show(this,
                $"The addon in {s.Client.Label} has different files from the ones the companion comes with (maybe a newer or edited copy). Replace it?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _addonMsg.Text = $"Installing into {s.Client.Label}…";
        try
        {
            await Task.Run(() => AddonInstaller.Install(s.Client));
            bool wowRunning = System.Diagnostics.Process.GetProcesses().Any(p => p.ProcessName.StartsWith("Wow", StringComparison.OrdinalIgnoreCase));
            _addonMsg.Text = $"Installed in {s.Client.Label}." + (wowRunning ? " Restart WoW (exit to desktop) to load it." : " Start WoW and it's there.");
        }
        catch (Exception e) { _addonMsg.Text = ""; MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        RefreshClients();
    }

    private Control MusicPage()
    {
        var zonePlaylists = new Button { Text = "Zone playlists…", AutoSize = true };
        zonePlaylists.Click += (_, _) => _host.ShowZonePlaylists();
        return Stack(
            _zoneMusic,
            Hint("Every city, zone, dungeon, raid and PvP map has a Relaxed and a Locked-in playlist."),
            Gap(),
            Bold("Default music"),
            _defaultSource,
            Hint("Used wherever you haven't picked a playlist yourself (in game, or in Zone playlists)."),
            Row(zonePlaylists, Hint("Pick Spotify playlists, radio stations or music folders per location.")),
            Gap(),
            Bold("Volume"),
            Row(Label("Volume buttons change it by"), _volumeStep, Label("%")),
            Row(Label("Lower the music to"), _duck, Label("% on a queue pop or death")),
            Gap(),
            Bold("Level-up fanfare"),
            Row(Label("Play it for"), _fanfare, Label("seconds, then go back to the location's music")));
    }

    private Control SpotifyPage()
    {
        var dashboard = new LinkLabel { Text = "Open the Spotify developer dashboard", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        dashboard.LinkClicked += (_, _) => Open("https://developer.spotify.com/dashboard");
        var redirect = new TextBox { Text = RedirectUri, ReadOnly = true, Width = 260 };
        var copy = new Button { Text = "Copy", AutoSize = true };
        copy.Click += (_, _) => { try { Clipboard.SetText(RedirectUri); copy.Text = "Copied"; } catch { } };
        _connect.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_clientId.Text))
            {
                MessageBox.Show(this, "Paste your app's Client ID first (steps 1–3).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                _clientId.Focus();
                return;
            }
            _useSpotify.Checked = true;
            if (!SaveSettings()) return;
            _connect.Enabled = false;
            try { await _host.ConnectSpotifyAsync(); } finally { _connect.Enabled = true; RefreshSpotify(); }
        };
        _disconnect.Click += async (_, _) => { await _host.DisconnectSpotifyAsync(); RefreshSpotify(); };
        _clientId.TextChanged += (_, _) => { if (_clientId.Focused && _clientId.Text.Trim().Length > 0) _useSpotify.Checked = true; };

        return Stack(
            _spotifyStatus,
            Hint("Optional. Without Spotify the companion plays free radio stations or your music folders."),
            Gap(),
            Bold("Set up (once, needs Spotify Premium)"),
            Hint("Spotify only lets an app's owner and 5 invited people use it, so each player makes their own (free, 2 minutes):"),
            Row(Label("1."), dashboard, Label("→ Create app, tick Web API.")),
            Row(Label("2. Redirect URI, exactly:"), redirect, copy),
            Row(Label("3. Client ID from the app's page:"), _clientId),
            Row(Label("4."), _connect, _disconnect),
            Gap(),
            _useSpotify,
            Hint("Untick to stop using Spotify without signing out."));
    }

    private Control EventsPage()
    {
        _events.Columns.Add(new DataGridViewTextBoxColumn { Name = "key", Visible = false });
        _events.Columns.Add(new DataGridViewTextBoxColumn { Name = "event", HeaderText = "When", ReadOnly = true, FillWeight = 180 });
        var action = new DataGridViewComboBoxColumn { Name = "action", HeaderText = "Do", FillWeight = 140, DisplayMember = "Label", ValueMember = "Value", FlatStyle = FlatStyle.Flat };
        action.DataSource = Actions.Select(a => new { a.Value, a.Label }).ToList();
        _events.Columns.Add(action);
        _events.Columns.Add(new DataGridViewTextBoxColumn { Name = "slot", HeaderText = "Slot", FillWeight = 40 });
        _events.Columns.Add(new DataGridViewTextBoxColumn { Name = "revert", HeaderText = "Undo after (s)", FillWeight = 60 });
        _events.DataError += (_, e) => e.ThrowException = false;

        _slots.Columns.Add(new DataGridViewTextBoxColumn { Name = "slot", HeaderText = "Slot", ReadOnly = true, FillWeight = 30 });
        _slots.Columns.Add(new DataGridViewTextBoxColumn { Name = "uri", HeaderText = "Spotify link, radio URL or folder", FillWeight = 300 });

        var events = new Panel { Dock = DockStyle.Fill };
        events.Controls.Add(_events);
        var slots = new Panel { Dock = DockStyle.Bottom, Height = 150 };
        slots.Controls.Add(_slots);
        slots.Controls.Add(new Label { Text = "Playlist slots (for \"Play a playlist slot\" and the addon's slot buttons)", Dock = DockStyle.Top, Height = 22, Padding = new Padding(0, 6, 0, 0) });
        var top = Hint("What happens on game events. * Only used when location music is off (Music tab). \"Undo after\" makes Lower the music go back up by itself.");
        top.Dock = DockStyle.Top;
        var page = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        page.Controls.Add(events);
        page.Controls.Add(slots);
        page.Controls.Add(top);
        return page;
    }

    private Control AdvancedPage()
    {
        var openConfig = new Button { Text = "Open config.json", AutoSize = true };
        openConfig.Click += (_, _) => Open(AppConfig.FilePath);
        var openFolder = new Button { Text = "Open settings folder", AutoSize = true };
        openFolder.Click += (_, _) => Open(AppConfig.Dir);
        var reset = new Button { Text = "Reset detection to defaults", AutoSize = true };
        reset.Click += (_, _) => { var d = new DetectionConfig(); _preamble.Value = (decimal)d.MinPreambleDb; _margin.Value = (decimal)d.MinSymbolMarginDb; _debounce.Value = (decimal)d.DebounceMs; };
        return Stack(
            Bold("Signal detection"),
            Hint("Only change these if signals are missed or misread (the debug window shows the levels)."),
            Row(Label("Start tones must be"), _preamble, Label("dB above the noise")),
            Row(Label("Each tone must win by"), _margin, Label("dB")),
            Row(Label("Ignore repeats within"), _debounce, Label("ms")),
            Row(Label("Wait"), _decisionDelay, Label("ms after a location signal before switching music")),
            reset,
            Gap(),
            Bold("WoW program names"),
            _processNames,
            Hint("Comma-separated. The companion listens to these programs' audio."),
            Gap(),
            Row(openConfig, openFolder),
            Hint("Everything here is saved in config.json; you can still edit it by hand."));
    }

    // ------------------------------------------------------------------ load / save

    private void Fill()
    {
        _autostart.Checked = _host.StartsWithWindows;
        _autoUpdate.Checked = _cfg.AutoUpdate;
        _keepAddon.Checked = _cfg.KeepAddonUpdated;
        _wowFolder.Text = _cfg.WowClientFolder;
        _zoneMusic.Checked = _cfg.ZoneMusic.Enabled;
        Select(_defaultSource, (_cfg.ZoneMusic.DefaultSource ?? "auto").ToLowerInvariant());
        _fanfare.Value = Math.Clamp(_cfg.FanfareSeconds, (int)_fanfare.Minimum, (int)_fanfare.Maximum);
        _volumeStep.Value = Math.Clamp((decimal)Math.Round(_cfg.VolumeStep * 100), _volumeStep.Minimum, _volumeStep.Maximum);
        _duck.Value = Math.Clamp((decimal)Math.Round(_cfg.DuckLevel * 100), _duck.Minimum, _duck.Maximum);
        _clientId.Text = _cfg.Spotify.ClientId;
        _useSpotify.Checked = _cfg.Spotify.UseWebApi;
        _preamble.Value = Math.Clamp((decimal)_cfg.Detection.MinPreambleDb, _preamble.Minimum, _preamble.Maximum);
        _margin.Value = Math.Clamp((decimal)_cfg.Detection.MinSymbolMarginDb, _margin.Minimum, _margin.Maximum);
        _debounce.Value = Math.Clamp((decimal)_cfg.Detection.DebounceMs, _debounce.Minimum, _debounce.Maximum);
        _decisionDelay.Value = Math.Clamp(_cfg.ZoneMusic.DecisionDelayMs, (int)_decisionDelay.Minimum, (int)_decisionDelay.Maximum);
        _processNames.Text = string.Join(", ", _cfg.WowProcessNames);

        _events.Rows.Clear();
        foreach (var evt in _host.EventNames)
        {
            var ea = _cfg.EventMap.GetValueOrDefault(evt);
            var act = ea?.Action ?? (evt == "EVT_LEVEL_UP" ? "FANFARE" : "NONE");
            if (!Actions.Any(a => a.Value == act)) act = "NONE";
            _events.Rows.Add(evt, EventLabels.GetValueOrDefault(evt, evt), act, ea?.Slot?.ToString() ?? "",
                ea?.AutoRevertMs is int ms ? (ms / 1000.0).ToString("0.#") : "");
        }
        _slots.Rows.Clear();
        // slots 0–9, plus any higher ones already in use
        var shown = Enumerable.Range(0, 10).Select(i => i.ToString())
            .Union(_cfg.PlaylistSlots.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key))
            .OrderBy(k => int.TryParse(k, out var n) ? n : 999);
        foreach (var k in shown) _slots.Rows.Add(k, _cfg.PlaylistSlots.GetValueOrDefault(k) ?? "");
        RefreshSpotify();
        RefreshClients();
    }

    private void RefreshSpotify()
    {
        _spotifyStatus.Text = _host.SpotifyStatus;
        bool signedIn = _host.SpotifyStatus.Contains("connected as", StringComparison.OrdinalIgnoreCase)
                        || _host.SpotifyStatus.Contains("signed in", StringComparison.OrdinalIgnoreCase);
        _connect.Text = signedIn ? "Reconnect…" : "Connect Spotify…";
        _disconnect.Enabled = signedIn;
    }

    /// <summary>Validates, writes config.json and applies it. Returns false (and says why) if something's wrong.</summary>
    private bool SaveSettings()
    {
        _events.EndEdit(); _slots.EndEdit();
        var c = Clone(_host.Config);   // start from what's on disk now, so settings this window doesn't show survive
        var problems = new List<string>();

        c.AutoUpdate = _autoUpdate.Checked;
        c.KeepAddonUpdated = _keepAddon.Checked;
        var folder = _wowFolder.Text.Trim();
        if (folder.Length > 0 && !Directory.Exists(Path.Combine(folder, "WTF")) && !Directory.Exists(Path.Combine(folder, "Interface")))
            problems.Add("The WoW client folder should be the one with WTF and Interface in it.");
        c.WowClientFolder = folder;
        c.ZoneMusic.Enabled = _zoneMusic.Checked;
        c.ZoneMusic.DefaultSource = Selected(_defaultSource);
        c.ZoneMusic.DecisionDelayMs = (int)_decisionDelay.Value;
        c.FanfareSeconds = (int)_fanfare.Value;
        c.VolumeStep = (float)_volumeStep.Value / 100f;
        c.DuckLevel = (float)_duck.Value / 100f;
        var oldId = c.Spotify.ClientId;
        c.Spotify.ClientId = _clientId.Text.Trim();
        bool newId = c.Spotify.ClientId.Length > 0 && c.Spotify.ClientId != oldId;   // adding an ID means "use Spotify"
        c.Spotify.UseWebApi = (_useSpotify.Checked || newId) && c.Spotify.ClientId.Length > 0;
        _useSpotify.Checked = c.Spotify.UseWebApi;
        c.Detection.MinPreambleDb = (double)_preamble.Value;
        c.Detection.MinSymbolMarginDb = (double)_margin.Value;
        c.Detection.DebounceMs = (double)_debounce.Value;
        var names = _processNames.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (names.Count == 0) problems.Add("Add at least one WoW program name (e.g. Wow.exe).");
        else c.WowProcessNames = names;

        var map = new Dictionary<string, EventAction>(c.EventMap);
        foreach (DataGridViewRow row in _events.Rows)
        {
            var key = row.Cells["key"].Value as string;
            if (key == null) continue;
            var act = row.Cells["action"].Value as string ?? "NONE";
            int? slot = int.TryParse(row.Cells["slot"].Value as string, out var s) ? s : null;
            int? revert = double.TryParse(row.Cells["revert"].Value as string, out var sec) && sec > 0 ? (int)(sec * 1000) : null;
            if (act == "PLAYLIST_SLOT" && slot is null) problems.Add($"\"{row.Cells["event"].Value}\" plays a slot: say which slot number.");
            if (act == "NONE" && !map.ContainsKey(key)) continue;   // don't clutter the file with unused events
            map[key] = new EventAction { Action = act, Slot = act == "PLAYLIST_SLOT" ? slot : null, AutoRevertMs = act == "DUCK_ON" ? revert : null };
        }
        c.EventMap = map;

        var slots = new Dictionary<string, string>(c.PlaylistSlots);   // slots beyond the ones shown stay as they are
        foreach (DataGridViewRow row in _slots.Rows)
        {
            var raw = (row.Cells["uri"].Value as string)?.Trim() ?? "";
            if (raw.Length > 0 && MusicUri.Normalize(raw) == null) problems.Add($"Slot {row.Cells["slot"].Value}: not a Spotify link, radio URL or folder.");
            slots[(string)row.Cells["slot"].Value!] = raw.Length > 0 ? MusicUri.Normalize(raw) ?? raw : "";
        }
        c.PlaylistSlots = slots;

        if (problems.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", problems), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        try
        {
            c.Save();
            if (_host.StartsWithWindows != _autostart.Checked) _host.StartsWithWindows = _autostart.Checked;
            _cfg = c;
            _host.ConfigSaved();
            RefreshSpotify();
            return true;
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "Couldn't save the settings: " + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static AppConfig Clone(AppConfig c) => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(c)) ?? new AppConfig();

    // ------------------------------------------------------------------ little layout helpers

    private static TabPage Page(string title, Control content)
    {
        var p = new TabPage(title) { Padding = new Padding(0), Name = title };
        content.Dock = DockStyle.Fill;
        p.Controls.Add(content);
        return p;
    }

    private static Control Stack(params Control[] items)
    {
        var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(14, 12, 14, 12) };
        foreach (var c in items) { c.Margin = new Padding(0, 2, 0, 2); f.Controls.Add(c); }
        return f;
    }

    private static Control Row(params Control[] items)
    {
        var f = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
        foreach (var c in items) { c.Margin = new Padding(0, 0, 6, 0); f.Controls.Add(c); }
        return f;
    }

    private static Label Label(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private static Label Bold(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Padding = new Padding(0, 4, 0, 0) };
    private static Label Hint(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = SystemColors.GrayText };
    private static Control Gap() => new Panel { Height = 8, Width = 10 };
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true };

    private static NumericUpDown Num(decimal min, decimal max, decimal step, int decimals = 0) =>
        new() { Minimum = min, Maximum = max, Increment = step, DecimalPlaces = decimals, Width = 70 };

    private sealed record Choice(string Value, string Label) { public override string ToString() => Label; }

    private static ComboBox Combo(params (string Value, string Label)[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
        foreach (var (v, l) in items) c.Items.Add(new Choice(v, l));
        return c;
    }

    private static void Select(ComboBox c, string value)
    {
        foreach (Choice ch in c.Items) if (ch.Value == value) { c.SelectedItem = ch; return; }
        c.SelectedIndex = 0;
    }

    private static string Selected(ComboBox c) => (c.SelectedItem as Choice)?.Value ?? "auto";

    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window,
        SelectionMode = DataGridViewSelectionMode.CellSelect, EditMode = DataGridViewEditMode.EditOnEnter,
    };

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
