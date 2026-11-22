using System.Diagnostics;

namespace AudioVibes.App;

/// <summary>
/// Shows every location's two playlists (Relaxed and Locked-in; yours or the default) and lets the player
/// override per location or per vibe, per mode. In-game choices (made in the addon) win over these.
/// </summary>
public sealed class ZonePlaylistsForm : Form
{
    private readonly ZoneMusic _music;
    private readonly Func<AppConfig> _config;
    private readonly DataGridView _zones, _vibes;
    private readonly Label _now, _hint;
    private readonly Button _lookup;

    private static readonly string[] TypeOrder = ["city", "zone", "dungeon", "raid", "pvp"];
    // column name → mode
    private static readonly (string Col, string Mode)[] MineCols = [("mineRelaxed", Modes.Relaxed), ("mineLocked", Modes.Locked)];

    public ZonePlaylistsForm(ZoneMusic music, Func<AppConfig> config)
    {
        _music = music; _config = config;
        Text = "WoW Audio Vibes – zone playlists";
        Width = 1180; Height = 700;
        Font = new Font("Segoe UI", 9);
        StartPosition = FormStartPosition.CenterScreen;

        _now = new Label { Dock = DockStyle.Top, Height = 28, Padding = new Padding(8, 6, 0, 0), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        _hint = new Label
        {
            Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 2, 8, 0), ForeColor = SystemColors.GrayText,
            Text = "Every location has a Relaxed and a Locked-in playlist. Paste a Spotify link (or spotify:playlist:…) in either column " +
                   "to override it; leave it empty for the default, or type off to leave the music alone. Playlists set in game win over these.",
        };

        _zones = MakeGrid();
        _zones.Columns.Add(Col("key", "Key", 95, true));
        _zones.Columns.Add(Col("zone", "Zone", 160, true));
        _zones.Columns.Add(Col("type", "Type", 65, true));
        _zones.Columns.Add(Col("mineRelaxed", "Relaxed: yours", 190, false));
        _zones.Columns.Add(Col("defRelaxed", "Relaxed: default", 165, true));
        _zones.Columns.Add(Col("mineLocked", "Locked-in: yours", 190, false));
        _zones.Columns.Add(Col("defLocked", "Locked-in: default", 165, true));

        _vibes = MakeGrid();
        _vibes.Columns.Add(Col("key", "Key", 95, true));
        _vibes.Columns.Add(Col("zones", "Zones", 220, true));
        _vibes.Columns.Add(Col("mineRelaxed", "Relaxed: yours", 190, false));
        _vibes.Columns.Add(Col("defRelaxed", "Relaxed: default", 165, true));
        _vibes.Columns.Add(Col("mineLocked", "Locked-in: yours", 190, false));
        _vibes.Columns.Add(Col("defLocked", "Locked-in: default", 165, true));

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var tz = new TabPage("Zones"); tz.Controls.Add(_zones);
        var tv = new TabPage("Vibes"); tv.Controls.Add(_vibes);
        tabs.TabPages.Add(tz); tabs.TabPages.Add(tv);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6), FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        var save = new Button { Text = "Save", AutoSize = true };
        save.Click += (_, _) => SaveOverrides();
        _lookup = new Button { Text = "Look up default playlists", AutoSize = true };
        _lookup.Click += async (_, _) => await LookUpDefaultsAsync();
        var open = new Button { Text = "Open selected cell in Spotify", AutoSize = true };
        open.Click += (_, _) => OpenSelected(tabs.SelectedTab == tz ? _zones : _vibes);
        buttons.Controls.AddRange([close, save, _lookup, open]);

        Controls.Add(tabs);
        Controls.Add(buttons);
        Controls.Add(_hint);
        Controls.Add(_now);

        _music.Changed += OnChanged;
        FormClosed += (_, _) => _music.Changed -= OnChanged;
        Load += async (_, _) => { await FillAsync(); ShowNow(_music.Current); };
    }

    private void OnChanged(ZoneChoice c)
    {
        if (IsHandleCreated) BeginInvoke(() => ShowNow(c));
    }

    private void ShowNow(ZoneChoice? c) =>
        _now.Text = c == null ? "Now: (no zone received yet; zone into somewhere or type /vibes zone in game)"
                              : $"Now: {c.ZoneName}  ·  {Modes.Name(c.Mode)}  →  {c.VibeName}  →  {c.PlaylistLabel}   [{c.Source}]";

    private static DataGridView MakeGrid() => new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window,
    };

    private static DataGridViewTextBoxColumn Col(string name, string header, int weight, bool ro) => new()
    {
        Name = name, HeaderText = header, FillWeight = weight, ReadOnly = ro,
        DefaultCellStyle = ro ? new DataGridViewCellStyle { ForeColor = SystemColors.ControlDarkDark } : new DataGridViewCellStyle(),
    };

    /// <summary>Value stored for a key in a mode: "key@mode" if present, else the both-modes "key".</summary>
    private static string Stored(Dictionary<string, string> d, string key, string mode) =>
        d.GetValueOrDefault(key + "@" + mode) ?? d.GetValueOrDefault(key) ?? "";

    private async Task FillAsync()
    {
        var cfg = _config();
        var cat = _music.Catalog;
        var seen = _music.Seen;

        // Zones: built-in list plus anything visited or learned in game (Forever-only zones etc.)
        var rows = new List<(int order, string name, object[] cells)>();
        foreach (var key in _music.AllKeys())
        {
            var category = seen.GetValueOrDefault(key)?.Category;
            var type = _music.TypeOf(key, category);
            var vibe = cat.VibeFor(key, category);
            var name = _music.NameOf(key);
            rows.Add((Array.IndexOf(TypeOrder, type) is var i && i >= 0 ? i : 9, name,
                [key, name + (cat.Zones.ContainsKey(key) ? "" : "  (visited)"), type,
                 Stored(cfg.ZonePlaylists, key, Modes.Relaxed), DefaultLabel(vibe, Modes.Relaxed),
                 Stored(cfg.ZonePlaylists, key, Modes.Locked), DefaultLabel(vibe, Modes.Locked)]));
        }
        _zones.Rows.Clear();
        foreach (var r in rows.OrderBy(r => r.order).ThenBy(r => r.name)) _zones.Rows.Add(r.cells);

        _vibes.Rows.Clear();
        foreach (var (vkey, _) in cat.Vibes)
        {
            var members = cat.Zones.Where(z => z.Value.Vibe == vkey).Select(z => z.Value.Name).ToList();
            var zonesText = members.Count == 0 ? "(fallback)" : string.Join(", ", members.Take(3)) + (members.Count > 3 ? $" +{members.Count - 3}" : "");
            _vibes.Rows.Add(vkey, zonesText,
                Stored(cfg.VibePlaylists, vkey, Modes.Relaxed), DefaultLabel(vkey, Modes.Relaxed),
                Stored(cfg.VibePlaylists, vkey, Modes.Locked), DefaultLabel(vkey, Modes.Locked));
        }
        await Task.CompletedTask;
    }

    private string DefaultLabel(string vibe, string mode)
    {
        var mood = _music.Catalog.Vibes.GetValueOrDefault(vibe)?.For(mode);
        if (mood == null) return "";
        if (SpotifyUri.Normalize(mood.Uri) is not null) return mood.Name;
        if (_music.CachedDefault(vibe, mode) is { } r) return $"{r.Name}  ({mood.Name})";
        return $"{mood.Name}: search \"{mood.Search}\"";
    }

    private async Task LookUpDefaultsAsync()
    {
        _lookup.Enabled = false;
        var old = _lookup.Text;
        int ok = 0, n = 0, total = _music.Catalog.Vibes.Count * Modes.All.Length;
        string? error = null;
        try
        {
            foreach (var vibe in _music.Catalog.Vibes.Keys)
                foreach (var mode in Modes.All)
                {
                    _lookup.Text = $"Looking up {++n}/{total}…";
                    try { if (await _music.ResolveDefaultAsync(vibe, mode) != null) ok++; }
                    catch (Exception e) { error = e.Message; goto done; }
                }
            done:;
        }
        finally { _lookup.Text = old; _lookup.Enabled = true; }
        await FillAsync();
        if (error != null) MessageBox.Show(this, "Couldn't search Spotify: " + error + "\n\nIs Spotify connected (tray → Connect Spotify)?", "WoW Audio Vibes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else if (ok < total) MessageBox.Show(this, $"Found defaults for {ok} of {total} vibe moods.", "WoW Audio Vibes");
    }

    private void OpenSelected(DataGridView grid)
    {
        var cell = grid.CurrentCell;
        if (cell == null) return;
        var colName = grid.Columns[cell.ColumnIndex].Name;
        var mode = colName.EndsWith("Locked") ? Modes.Locked : Modes.Relaxed;
        var key = grid.Rows[cell.RowIndex].Cells["key"].Value as string ?? "";
        string? uri = SpotifyUri.Normalize(grid.Rows[cell.RowIndex].Cells[mode == Modes.Locked ? "mineLocked" : "mineRelaxed"].Value as string);
        if (uri == null)
        {
            var vibe = grid == _vibes ? key : _music.Catalog.VibeFor(key, _music.Seen.GetValueOrDefault(key)?.Category);
            uri = (grid == _zones ? SpotifyUri.Normalize(_config().VibeOverride(vibe, mode)) : null)
                  ?? SpotifyUri.Normalize(_music.Catalog.Vibes.GetValueOrDefault(vibe)?.For(mode).Uri)
                  ?? _music.CachedDefault(vibe, mode)?.Uri;
        }
        if (uri == null) { MessageBox.Show(this, "No playlist yet. Click \"Look up default playlists\" first.", "WoW Audio Vibes"); return; }
        var parts = uri.Split(':');
        var url = parts.Length == 3 ? $"https://open.spotify.com/{parts[1]}/{parts[2]}" : uri;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void SaveOverrides()
    {
        _zones.EndEdit(); _vibes.EndEdit();
        var bad = new List<string>();
        var zones = Collect(_zones, bad);
        var vibes = Collect(_vibes, bad);
        if (bad.Count > 0)
        {
            MessageBox.Show(this, "These aren't Spotify playlist links:\n\n" + string.Join("\n", bad.Take(10)) +
                "\n\nUse a link like https://open.spotify.com/playlist/… or spotify:playlist:…, or 'off'.", "WoW Audio Vibes",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var cfg = _config();
        cfg.ZonePlaylists = zones;
        cfg.VibePlaylists = vibes;
        try
        {
            cfg.Save();
            _music.ForgetCurrent();
            MessageBox.Show(this, "Saved. Changes apply on your next zone change (or type /vibes zone in game).", "WoW Audio Vibes");
        }
        catch (Exception e) { MessageBox.Show(this, "Couldn't save config.json: " + e.Message, "WoW Audio Vibes"); }
    }

    /// <summary>Writes "key" when both modes match (simpler config), else "key@relaxed" / "key@locked".</summary>
    private static Dictionary<string, string> Collect(DataGridView grid, List<string> bad)
    {
        var d = new Dictionary<string, string>();
        foreach (DataGridViewRow row in grid.Rows)
        {
            var key = row.Cells["key"].Value as string;
            if (key == null) continue;
            var vals = new Dictionary<string, string>();
            foreach (var (col, mode) in MineCols)
            {
                var raw = (row.Cells[col].Value as string)?.Trim();
                if (string.IsNullOrEmpty(raw)) continue;
                if (SpotifyUri.IsOff(raw)) vals[mode] = "off";
                else if (SpotifyUri.Normalize(raw) is { } uri) vals[mode] = uri;
                else bad.Add($"{row.Cells[1].Value} ({Modes.Name(mode)}): {raw}");
            }
            if (vals.Count == 2 && vals[Modes.Relaxed] == vals[Modes.Locked]) d[key] = vals[Modes.Relaxed];
            else foreach (var (mode, v) in vals) d[key + "@" + mode] = v;
        }
        return d;
    }
}
