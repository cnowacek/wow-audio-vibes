using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioVibes.App;

/// <summary>The two vibe modes. Every location has one playlist per mode.</summary>
public static class Modes
{
    public const string Relaxed = "relaxed", Locked = "locked";
    public static readonly string[] All = [Relaxed, Locked];
    public static string Name(string mode) => mode == Locked ? "Locked-in" : "Relaxed";
    public static string? Parse(string? s) => s == Relaxed || s == Locked ? s : null;
    /// <summary>Auto: relaxed in cities and the open world, locked-in in dungeons, raids and PvP.</summary>
    public static string Auto(string type) => type is "dungeon" or "raid" or "pvp" ? Locked : Relaxed;
}

public sealed class VibeMood
{
    public string Name { get; set; } = "";
    /// <summary>Spotify search used to pick the default playlist (resolved once, then cached).</summary>
    public string Search { get; set; } = "";
    /// <summary>Optional pinned default playlist; wins over <see cref="Search"/>.</summary>
    public string? Uri { get; set; }
}

/// <summary>A vibe: the top-level fields are its Locked-in mood, <see cref="Relaxed"/> its Relaxed one.</summary>
public sealed class VibeDef
{
    public string Name { get; set; } = "";
    public string Search { get; set; } = "";
    public string? Uri { get; set; }
    public VibeMood? Relaxed { get; set; }

    public VibeMood For(string mode) =>
        mode == Modes.Relaxed && Relaxed != null ? Relaxed : new VibeMood { Name = Name, Search = Search, Uri = Uri };
}

public sealed class ZoneDef
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "zone"; // city | zone | dungeon | raid | pvp
    public string Vibe { get; set; } = "adventure";
}

/// <summary>Built-in defaults (zones.json, embedded): zone → vibe, vibe → default playlist.</summary>
public sealed class ZoneCatalog
{
    public Dictionary<string, VibeDef> Vibes { get; set; } = new();
    public Dictionary<string, string> Categories { get; set; } = new();
    public Dictionary<string, ZoneDef> Zones { get; set; } = new();

    public const string FallbackVibe = "adventure";

    public static ZoneCatalog Load()
    {
        using var s = typeof(ZoneCatalog).Assembly.GetManifestResourceStream("AudioVibes.zones.json")
            ?? throw new InvalidOperationException("embedded zones.json missing");
        return JsonSerializer.Deserialize<ZoneCatalog>(s, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) ?? new ZoneCatalog();
    }

    /// <summary>Display name for a zone key, e.g. "Elwynn Forest", or "Instance 1234" for unknown ones.</summary>
    public string NameOf(string key, string? learnedName = null)
    {
        if (Zones.TryGetValue(key, out var z)) return z.Name;
        if (!string.IsNullOrWhiteSpace(learnedName)) return learnedName!;
        var (kind, id) = Split(key);
        return kind == "instance" ? $"Instance {id}" : $"Zone {id}";
    }

    public string VibeFor(string? zoneKey, string? category)
    {
        if (zoneKey != null && Zones.TryGetValue(zoneKey, out var z) && Vibes.ContainsKey(z.Vibe)) return z.Vibe;
        if (category != null && Categories.TryGetValue(category, out var v) && Vibes.ContainsKey(v)) return v;
        return FallbackVibe;
    }

    public string VibeName(string vibe, string mode = Modes.Locked) => Vibes.TryGetValue(vibe, out var v) ? v.For(mode).Name : vibe;

    public static (string Kind, int Id) Split(string key)
    {
        int i = key.IndexOf(':');
        return i > 0 && int.TryParse(key[(i + 1)..], out int id) ? (key[..i], id) : (key, 0);
    }

    public static string TypeFromCategory(string? category) => category switch
    {
        "EVT_ENTER_CITY" => "city",
        "EVT_ENTER_DUNGEON" => "dungeon",
        "EVT_ENTER_RAID" => "raid",
        "EVT_ENTER_PVP" => "pvp",
        _ => "zone",
    };
}

/// <summary>Accepts "spotify:playlist:ID", "https://open.spotify.com/playlist/ID?si=..", albums/artists too.</summary>
public static class SpotifyUri
{
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        if (s.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)) return s;
        if (Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Host.EndsWith("spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.StartsWith("intl-", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (parts.Length >= 2 && parts[0] is "playlist" or "album" or "artist" or "show")
                return $"spotify:{parts[0]}:{parts[1]}";
        }
        return null;
    }

    public static bool IsOff(string? s) => s != null && s.Trim().ToLowerInvariant() is "off" or "none";
}
