using System.Globalization;
using System.Text;
using Serilog;

namespace AudioVibes.App;

/// <summary>
/// Minimal parser for WoW SavedVariables files (Lua assignments of literal tables):
///   AudioVibesDB = { ["locations"] = { ["map:1429"] = { ["uri"] = "...", }, }, ["debug"] = false, }
/// Values: string, double, bool, null (nil) and <see cref="LuaTable"/>. Comments are skipped.
/// </summary>
public static class LuaSavedVariables
{
    public sealed class LuaTable : Dictionary<object, object?>
    {
        public string? Str(string k) => TryGetValue(k, out var v) ? v as string : null;
        public bool? Bool(string k) => TryGetValue(k, out var v) && v is bool b ? b : null;
        public LuaTable? Table(string k) => TryGetValue(k, out var v) ? v as LuaTable : null;
    }

    public static Dictionary<string, object?> Parse(string src)
    {
        var p = new Parser(src);
        var globals = new Dictionary<string, object?>();
        p.Skip();
        while (!p.End)
        {
            string name = p.Ident();
            p.Expect('=');
            globals[name] = p.Value();
            p.Skip();
            if (p.Peek == ';') { p.Pos++; p.Skip(); }
        }
        return globals;
    }

    private sealed class Parser(string s)
    {
        public int Pos;
        public bool End => Pos >= s.Length;
        public char Peek => Pos < s.Length ? s[Pos] : '\0';

        public void Skip()
        {
            while (Pos < s.Length)
            {
                char c = s[Pos];
                if (char.IsWhiteSpace(c)) { Pos++; continue; }
                if (c == '-' && Pos + 1 < s.Length && s[Pos + 1] == '-')
                {
                    Pos += 2;
                    if (Pos + 1 < s.Length && s[Pos] == '[' && s[Pos + 1] == '[')
                    {
                        int e = s.IndexOf("]]", Pos, StringComparison.Ordinal);
                        Pos = e < 0 ? s.Length : e + 2;
                    }
                    else
                    {
                        while (Pos < s.Length && s[Pos] != '\n') Pos++;
                    }
                    continue;
                }
                break;
            }
        }

        public void Expect(char c)
        {
            Skip();
            if (Peek != c) throw Error($"expected '{c}'");
            Pos++;
        }

        private FormatException Error(string what)
        {
            int line = 1;
            for (int i = 0; i < Math.Min(Pos, s.Length); i++) if (s[i] == '\n') line++;
            return new FormatException($"{what} at line {line}");
        }

        public string Ident()
        {
            Skip();
            int start = Pos;
            while (Pos < s.Length && (char.IsLetterOrDigit(s[Pos]) || s[Pos] == '_')) Pos++;
            if (start == Pos) throw Error("expected a name");
            return s[start..Pos];
        }

        public object? Value()
        {
            Skip();
            char c = Peek;
            if (c == '{') return Table();
            if (c == '"' || c == '\'') return String();
            if (c == '-' || c == '.' || char.IsDigit(c)) return Number();
            string id = Ident();
            return id switch
            {
                "true" => true,
                "false" => false,
                "nil" => null,
                _ => throw Error($"unexpected '{id}'"),
            };
        }

        private LuaTable Table()
        {
            Expect('{');
            var t = new LuaTable();
            double next = 1;
            while (true)
            {
                Skip();
                if (Peek == '}') { Pos++; return t; }
                object key;
                if (Peek == '[')
                {
                    Pos++;
                    var k = Value() ?? throw Error("nil table key");
                    Expect(']');
                    Expect('=');
                    key = k;
                }
                else
                {
                    int save = Pos;
                    if (char.IsLetter(Peek) || Peek == '_')
                    {
                        string id = Ident();
                        Skip();
                        if (Peek == '=') { Pos++; key = id; }
                        else { Pos = save; key = next++; }
                    }
                    else key = next++;
                }
                t[key] = Value();
                Skip();
                if (Peek == ',' || Peek == ';') Pos++;
                else if (Peek != '}') throw Error("expected ',' or '}'");
            }
        }

        private string String()
        {
            // Lua strings are bytes: literal text is UTF-8, \ddd escapes are raw bytes. Collect bytes, decode once.
            char q = s[Pos++];
            var bytes = new List<byte>();
            var buf = new byte[4];
            void Lit(char ch)
            {
                int n = Encoding.UTF8.GetBytes([ch], buf);
                for (int i = 0; i < n; i++) bytes.Add(buf[i]);
            }
            while (true)
            {
                if (Pos >= s.Length) throw Error("unterminated string");
                char c = s[Pos++];
                if (c == q) return Encoding.UTF8.GetString(bytes.ToArray());
                if (char.IsHighSurrogate(c) && Pos < s.Length)
                {
                    foreach (var b in Encoding.UTF8.GetBytes(new[] { c, s[Pos++] })) bytes.Add(b);
                    continue;
                }
                if (c != '\\') { Lit(c); continue; }
                char e = s[Pos++];
                switch (e)
                {
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    case '\n': bytes.Add((byte)'\n'); break;
                    default:
                        if (char.IsDigit(e))
                        {
                            int n = e - '0', digits = 1;
                            while (digits < 3 && Pos < s.Length && char.IsDigit(s[Pos])) { n = n * 10 + (s[Pos++] - '0'); digits++; }
                            bytes.Add((byte)n);
                        }
                        else Lit(e);   // \\  \"  \'  and anything else literal
                        break;
                }
            }
        }

        private double Number()
        {
            int start = Pos;
            if (Peek == '-') Pos++;
            while (Pos < s.Length && (char.IsDigit(s[Pos]) || s[Pos] is '.' or 'e' or 'E' or '+' or '-' or 'x' or 'X' || char.IsAsciiHexDigit(s[Pos]))) Pos++;
            var text = s[start..Pos];
            if (text.Contains("0x", StringComparison.OrdinalIgnoreCase))
            {
                bool neg = text.StartsWith('-');
                var v = long.Parse(text.TrimStart('-')[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return neg ? -v : v;
            }
            return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>Playlist chosen in game (Change playlist dialog).</summary>
public sealed record GamePlaylist(string Uri, string Name, bool Shuffle);

/// <summary>A location's (or type's) in-game playlists, one per vibe mode.</summary>
public sealed record ModePlaylists(GamePlaylist? Relaxed, GamePlaylist? Locked)
{
    public GamePlaylist? For(string mode) => mode == Modes.Relaxed ? Relaxed : Locked;
}

/// <summary>What the addon saved in AudioVibesDB that the companion cares about.</summary>
public sealed class GameOverrides
{
    public Dictionary<string, ModePlaylists> Locations { get; } = new();
    public Dictionary<string, ModePlaylists> Types { get; } = new();
    /// <summary>Locations fixed to one mode in game ("relaxed" | "locked").</summary>
    public Dictionary<string, string> LocationModes { get; } = new();
    public GamePlaylist? Hype { get; set; }
    public bool Shuffle { get; set; }
    /// <summary>"Pick up where I left off" (default on).</summary>
    public bool Resume { get; set; } = true;
    /// <summary>Names/types of locations the built-in list doesn't know (learned in game).</summary>
    public Dictionary<string, (string Name, string Type)> Seen { get; } = new();
    public string? SourceFile { get; set; }
    public DateTime LoadedAt { get; set; }

    public static GameOverrides Empty { get; } = new();

    public static GameOverrides FromLua(string src, string? file = null)
    {
        var o = new GameOverrides { SourceFile = file, LoadedAt = DateTime.Now };
        if (!LuaSavedVariables.Parse(src).TryGetValue("AudioVibesDB", out var dbObj) || dbObj is not LuaSavedVariables.LuaTable db)
            return o;

        static GamePlaylist? Pl(object? v)
        {
            if (v is not LuaSavedVariables.LuaTable t) return null;
            var uri = SpotifyUri.Normalize(t.Str("uri"));
            if (uri == null) return null;
            return new GamePlaylist(uri, t.Str("name") ?? "Custom playlist", t.Bool("shuffle") ?? false);
        }
        // 1.2: { relaxed = {…}, locked = {…} }. 1.1 and earlier: one playlist for both modes.
        static ModePlaylists? Modal(object? v)
        {
            if (v is not LuaSavedVariables.LuaTable t) return null;
            if (Pl(t) is { } both) return new ModePlaylists(both, both);
            var m = new ModePlaylists(Pl(t.Table("relaxed")), Pl(t.Table("locked")));
            return m.Relaxed == null && m.Locked == null ? null : m;
        }

        if (db.Table("locations") is { } locs)
            foreach (var (k, v) in locs) if (k is string key && Modal(v) is { } p) o.Locations[key] = p;
        if (db.Table("types") is { } types)
            foreach (var (k, v) in types) if (k is string key && Modal(v) is { } p) o.Types[key] = p;
        if (db.Table("modes") is { } modes)
            foreach (var (k, v) in modes) if (k is string key && Modes.Parse(v as string) is { } m) o.LocationModes[key] = m;
        o.Hype = Pl(db.TryGetValue("hype", out var h) ? h : null);
        var settings = db.Table("settings");
        o.Shuffle = settings?.Bool("shuffle") ?? false;
        o.Resume = settings?.Bool("resume") ?? true;
        if (db.Table("seen") is { } seen)
            foreach (var (k, v) in seen)
                if (k is string key && v is LuaSavedVariables.LuaTable t && t.Str("name") is { } n)
                    o.Seen[key] = (n, t.Str("type") ?? "zone");
        return o;
    }
}

/// <summary>
/// Watches WTF\Account\*\SavedVariables\AudioVibes.lua under the WoW client folder (WoW writes it on
/// /reload or logout) and raises <see cref="Changed"/> with the parsed overrides.
/// </summary>
public sealed class SavedVariablesWatcher : IDisposable
{
    private FileSystemWatcher? _fsw;
    private string? _clientDir;
    private readonly System.Threading.Timer _debounce;
    public GameOverrides Current { get; private set; } = GameOverrides.Empty;
    public event Action<GameOverrides>? Changed;
    public string? ClientDir => _clientDir;

    public SavedVariablesWatcher() => _debounce = new System.Threading.Timer(_ => Load(), null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>Point at a WoW client folder (the one containing Wow.exe and WTF\).</summary>
    public void SetClientDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || string.Equals(dir, _clientDir, StringComparison.OrdinalIgnoreCase)) return;
        var account = Path.Combine(dir, "WTF", "Account");
        _fsw?.Dispose();
        _fsw = null;
        _clientDir = dir;
        if (!Directory.Exists(account)) { Log.Information("no WTF\\Account under {Dir} yet", dir); return; }
        _fsw = new FileSystemWatcher(account, "AudioVibes.lua")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        FileSystemEventHandler h = (_, _) => _debounce.Change(500, Timeout.Infinite);
        _fsw.Changed += h; _fsw.Created += h; _fsw.Renamed += (_, _) => _debounce.Change(500, Timeout.Infinite);
        _fsw.EnableRaisingEvents = true;
        Log.Information("watching saved variables under {Dir}", account);
        Load();
    }

    /// <summary>The newest AudioVibes.lua across accounts (one player normally has one account).</summary>
    public static string? FindFile(string clientDir)
    {
        var account = Path.Combine(clientDir, "WTF", "Account");
        if (!Directory.Exists(account)) return null;
        return Directory.EnumerateFiles(account, "AudioVibes.lua", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "SavedVariables")
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    public void Load()
    {
        if (_clientDir == null) return;
        var file = FindFile(_clientDir);
        if (file == null) return;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                var o = GameOverrides.FromLua(File.ReadAllText(file), file);
                Current = o;
                Log.Information("in-game playlists loaded from {File}: {Loc} locations, {Types} types, hype {Hype}",
                    file, o.Locations.Count, o.Types.Count, o.Hype != null);
                Changed?.Invoke(o);
                return;
            }
            catch (IOException) { Thread.Sleep(200); }
            catch (Exception e) { Log.Warning(e, "could not parse {File}", file); return; }
        }
    }

    public void Dispose() { _fsw?.Dispose(); _debounce.Dispose(); }
}
