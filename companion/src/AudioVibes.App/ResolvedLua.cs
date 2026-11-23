using System.Text;
using Serilog;

namespace AudioVibes.App;

/// <summary>
/// Writes the addon's Resolved.lua: the playlist the companion has chosen for every location (defaults
/// and Zone-playlists-window overrides; the addon handles in-game playlists itself), plus its music setup
/// (default source, Spotify on or off). This is the only
/// thing that flows companion → game: WoW reads the file on login and /reload, so the addon can show
/// real playlist names and copyable links.
/// </summary>
public static class ResolvedLua
{
    public sealed record Entry(string? Uri, string? Name);

    private static readonly Dictionary<string, string> TypeCategory = new()
    {
        ["city"] = "EVT_ENTER_CITY", ["zone"] = "EVT_ENTER_OPEN_WORLD", ["dungeon"] = "EVT_ENTER_DUNGEON",
        ["raid"] = "EVT_ENTER_RAID", ["pvp"] = "EVT_ENTER_PVP",
    };

    /// <summary>Lua string literal; non-ASCII stays as UTF-8 (WoW reads Lua files as UTF-8 bytes).</summary>
    public static string Str(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 32) sb.Append('\\').Append(((int)c).ToString("000"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>One location's (or type's) companion choices, per vibe mode.</summary>
    public sealed record ModeEntries(Entry? Relaxed, Entry? Locked);

    private static string EntryLua(Entry e)
    {
        var s = "{ uri = " + Str(e.Uri!);
        if (!string.IsNullOrWhiteSpace(e.Name)) s += ", name = " + Str(e.Name!);
        return s + " }";
    }

    /// <summary>The companion's music setup, so the addon can tell what plays and grey out what can't work.
    /// DefaultSource: "spotify" | "radio" | "off" (auto already worked out); Spotify: Spotify playlists can be started.</summary>
    public sealed record SourceInfo(string DefaultSource, bool Spotify);

    public static string Render(IReadOnlyDictionary<string, ModeEntries> locations, IReadOnlyDictionary<string, ModeEntries> types, DateTime when,
                                SourceInfo? source = null)
    {
        static void Write(StringBuilder sb, IReadOnlyDictionary<string, ModeEntries> d)
        {
            foreach (var (k, e) in d.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var parts = new List<string>();
                if (e.Relaxed?.Uri != null) parts.Add("relaxed = " + EntryLua(e.Relaxed));
                if (e.Locked?.Uri != null) parts.Add("locked = " + EntryLua(e.Locked));
                if (parts.Count == 0) continue;
                sb.Append("    [").Append(Str(k)).Append("] = { ").Append(string.Join(", ", parts)).Append(" },\n");
            }
        }
        var sb = new StringBuilder();
        sb.Append("-- Written by the WoW Audio Vibes companion app: the playlist it has chosen for each location\n");
        sb.Append("-- (default playlists and playlists set in its Zone playlists window), so the addon can show real\n");
        sb.Append("-- names and copyable links. WoW reads this file on login and /reload. Safe to delete; the\n");
        sb.Append("-- companion writes it again.\n");
        sb.Append("AudioVibesResolved = {\n");
        sb.Append("  updated = ").Append(Str(when.ToString("yyyy-MM-dd HH:mm"))).Append(",\n");
        if (source != null)
            sb.Append("  source = { default = ").Append(Str(source.DefaultSource))
              .Append(", spotify = ").Append(source.Spotify ? "true" : "false").Append(" },\n");
        sb.Append("  locations = {\n"); Write(sb, locations); sb.Append("  },\n");
        sb.Append("  types = {\n"); Write(sb, types); sb.Append("  },\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>Works out every location's companion-side playlist (no network) and renders the file.</summary>
    public static async Task<(string Lua, int Count)> BuildAsync(ZoneMusic music)
    {
        static Entry ToEntry(ZoneChoice c) =>
            // Overrides from the window are bare URIs (no name); defaults carry the playlist's real name.
            new(c.Uri, MusicUri.LooksRaw(c.PlaylistLabel) || c.Uri == null ? null : c.PlaylistLabel);
        async Task<ModeEntries> Both(string? key, string? evt)
        {
            var r = await music.ChooseAsync(key, evt, resolveSearch: false, includeGame: false, mode: Modes.Relaxed);
            var l = await music.ChooseAsync(key, evt, resolveSearch: false, includeGame: false, mode: Modes.Locked);
            return new ModeEntries(r.Uri != null ? ToEntry(r) : null, l.Uri != null ? ToEntry(l) : null);
        }
        var locs = new Dictionary<string, ModeEntries>();
        foreach (var key in music.AllKeys())
        {
            var e = await Both(key, null);
            if (e.Relaxed != null || e.Locked != null) locs[key] = e;
        }
        var types = new Dictionary<string, ModeEntries>();
        foreach (var (type, evt) in TypeCategory)
        {
            var e = await Both(null, evt);
            if (e.Relaxed != null || e.Locked != null) types[type] = e;
        }
        // Keep the timestamp out of change detection: only rewrite when a playlist actually changed.
        return (Render(locs, types, DateTime.Now, new SourceInfo(music.DefaultSource, music.SpotifyReady)), locs.Count);
    }

    /// <summary>Writes the file if its playlists changed. Returns true if written.</summary>
    public static bool WriteIfChanged(string path, string lua)
    {
        static string Body(string s) => string.Join('\n', s.Split('\n').Where(l => !l.TrimStart().StartsWith("updated =")));
        try
        {
            if (File.Exists(path) && Body(File.ReadAllText(path)) == Body(lua)) return false;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, lua, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            Log.Warning(e, "could not write {Path}", path);
            return false;
        }
    }
}
