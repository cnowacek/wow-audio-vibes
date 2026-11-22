using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioVibes.App;

public sealed class EventAction
{
    public string Action { get; set; } = "NONE";
    public int? Slot { get; set; }
    public int? AutoRevertMs { get; set; }
}

public sealed class SpotifyConfig
{
    public string ClientId { get; set; } = "";
    public bool UseWebApi { get; set; }
}

public sealed class ZoneMusicConfig
{
    /// <summary>Play a playlist per zone. When on, EVT_ENTER_* events go to zone music instead of eventMap.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How long to collect the zone id + category before deciding (they arrive ~0.5 s apart).</summary>
    public int DecisionDelayMs { get; set; } = 1200;
}

public sealed class DetectionConfig
{
    public double MinPreambleDb { get; set; } = 12;
    public double MinSymbolMarginDb { get; set; } = 6;
    public double DebounceMs { get; set; } = 500;
}

public sealed class AppConfig
{
    public List<string> WowProcessNames { get; set; } = ["Wow.exe", "WowClassic.exe", "Wow_Forever.exe"];
    public float DuckLevel { get; set; } = 0.3f;
    public float VolumeStep { get; set; } = 0.1f;
    /// <summary>How long the level-up fanfare plays before going back to the location playlist.</summary>
    public int FanfareSeconds { get; set; } = 30;
    /// <summary>WoW client folder (contains Wow.exe and WTF\). Empty = detect from the running game.</summary>
    public string WowClientFolder { get; set; } = "";
    public SpotifyConfig Spotify { get; set; } = new();
    public Dictionary<string, string> PlaylistSlots { get; set; } = new() { ["0"] = "", ["1"] = "" };
    public Dictionary<string, EventAction> EventMap { get; set; } = new()
    {
        // EVT_ENTER_* (city/dungeon/raid/pvp/open world) are handled by zone music (zoneMusic.enabled).
        ["EVT_QUEUE_POP"] = new() { Action = "DUCK_ON", AutoRevertMs = 8000 },
        ["EVT_PLAYER_DEAD"] = new() { Action = "DUCK_ON" },
        ["EVT_PLAYER_ALIVE"] = new() { Action = "DUCK_OFF" },
        ["EVT_HYPE"] = new() { Action = "PLAYLIST_SLOT", Slot = 4 },   // used when no Hype playlist is set in game
        ["EVT_LEVEL_UP"] = new() { Action = "FANFARE" },
    };
    public DetectionConfig Detection { get; set; } = new();
    public ZoneMusicConfig ZoneMusic { get; set; } = new();
    /// <summary>Per-zone overrides: "map:1429" / "instance:36" → playlist URI or open.spotify.com link ("off" = leave music alone).</summary>
    public Dictionary<string, string> ZonePlaylists { get; set; } = new();
    /// <summary>Per-vibe overrides: "spooky" → playlist (applies to every zone with that vibe and no zone override).</summary>
    public Dictionary<string, string> VibePlaylists { get; set; } = new();

    /// <summary>Companion-window playlist for a location in a mode: "map:1454@relaxed", else "map:1454" (both modes).</summary>
    public string? ZoneOverride(string key, string mode) => Pick(ZonePlaylists, key, mode);

    /// <summary>Companion-window playlist for a vibe in a mode: "city_horde@relaxed", else "city_horde" (both modes).</summary>
    public string? VibeOverride(string vibe, string mode) => Pick(VibePlaylists, vibe, mode);

    private static string? Pick(Dictionary<string, string> d, string key, string mode)
    {
        if (d.TryGetValue(key + "@" + mode, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        if (d.TryGetValue(key, out v) && !string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WoW Audio Vibes");
    public static readonly string FilePath = Path.Combine(Dir, "config.json");
    /// <summary>Folder used before the app was renamed; its files are copied over once.</summary>
    private static readonly string LegacyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Murmur");

    /// <summary>First run under the new name: copy config, Spotify login, zone cache and recordings from the old folder.
    /// Copies (never moves), so the old folder stays as a backup.</summary>
    private static void MigrateLegacyFolder()
    {
        if (File.Exists(FilePath) || !File.Exists(Path.Combine(LegacyDir, "config.json"))) return;
        Directory.CreateDirectory(Dir);
        foreach (var name in new[] { "config.json", "spotify.token", "zone-state.json" })
        {
            var src = Path.Combine(LegacyDir, name);
            if (File.Exists(src)) File.Copy(src, Path.Combine(Dir, name), overwrite: false);
        }
        var rec = Path.Combine(LegacyDir, "recordings");
        if (Directory.Exists(rec))
        {
            Directory.CreateDirectory(Path.Combine(Dir, "recordings"));
            foreach (var f in Directory.GetFiles(rec))
            {
                var dst = Path.Combine(Dir, "recordings", Path.GetFileName(f));
                if (!File.Exists(dst)) File.Copy(f, dst);
            }
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppConfig LoadOrCreate()
    {
        try { MigrateLegacyFolder(); } catch (IOException) { /* best effort; defaults below */ }
        Directory.CreateDirectory(Dir);
        if (!File.Exists(FilePath))
        {
            var c = new AppConfig();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(c, Json));
            return c;
        }
        return Load() ?? new AppConfig();
    }

    /// <summary>Read-only load for hot reload. Never creates or overwrites the file: during an editor's
    /// temp-file swap config.json can briefly be missing. Returns null if it's still missing after retries.</summary>
    public static AppConfig? Load()
    {
        for (int i = 0; ; i++)
        {
            try
            {
                if (!File.Exists(FilePath)) throw new FileNotFoundException(FilePath);
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Json) ?? new();
            }
            catch (IOException) when (i < 10) { Thread.Sleep(100); }
            catch (IOException) { return null; }
        }
    }
}
