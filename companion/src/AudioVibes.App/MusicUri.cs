namespace AudioVibes.App;

public enum MusicKind { Spotify, Radio, Folder }

/// <summary>
/// Everything a location can play, as one string:
///   spotify:playlist:ID / spotify:album:ID   (open.spotify.com links are converted)
///   radio:https://host/stream.mp3            (any http(s) URL that isn't Spotify or YouTube; .pls/.m3u work too)
///   folder:C:\Music\Haunted                  (a drive path or \\server\share path works too)
/// </summary>
public static class MusicUri
{
    public const string RadioPrefix = "radio:", FolderPrefix = "folder:";

    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim().Trim('"');
        if (SpotifyUri.Normalize(s) is { } sp) return sp;
        if (s.StartsWith(RadioPrefix, StringComparison.OrdinalIgnoreCase)) s = s[RadioPrefix.Length..].Trim();
        else if (s.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase)) return Folder(s[FolderPrefix.Length..]);
        if (Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            return IsVideoSite(u) || u.Host.EndsWith("spotify.com", StringComparison.OrdinalIgnoreCase) ? null : RadioPrefix + s;
        return Folder(s);
    }

    private static string? Folder(string path)
    {
        path = path.Trim().Trim('"');
        bool drive = path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        bool unc = path.StartsWith(@"\\", StringComparison.Ordinal);
        return drive || unc ? FolderPrefix + path.Replace('/', '\\').TrimEnd('\\') : null;
    }

    /// <summary>Video sites can't be played as a radio stream; say so instead of failing at play time.</summary>
    public static bool IsVideoSite(Uri u) =>
        u.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase);

    public static MusicKind? KindOf(string? uri) =>
        uri == null ? null
        : uri.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase) ? MusicKind.Spotify
        : uri.StartsWith(RadioPrefix, StringComparison.OrdinalIgnoreCase) ? MusicKind.Radio
        : uri.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase) ? MusicKind.Folder
        : null;

    public static bool IsLocal(string? uri) => KindOf(uri) is MusicKind.Radio or MusicKind.Folder;

    /// <summary>The URL or path without the prefix.</summary>
    public static string Target(string uri) => KindOf(uri) switch
    {
        MusicKind.Radio => uri[RadioPrefix.Length..],
        MusicKind.Folder => uri[FolderPrefix.Length..],
        _ => uri,
    };

    /// <summary>Short label when there's no better name: "Radio: ice1.somafm.com", "Folder: Haunted".</summary>
    public static string Label(string uri) => KindOf(uri) switch
    {
        MusicKind.Radio => "Radio: " + (Uri.TryCreate(Target(uri), UriKind.Absolute, out var u) ? u.Host : Target(uri)),
        MusicKind.Folder => "Folder: " + (Path.GetFileName(Target(uri)) is { Length: > 0 } n ? n : Target(uri)),
        _ => uri,
    };

    /// <summary>A raw identifier (not a display name), so callers know to look for a better label.</summary>
    public static bool LooksRaw(string label) =>
        label.StartsWith("spotify:", StringComparison.Ordinal) || KindOf(label) != null;

    /// <summary>Link a person can open: open.spotify.com page, the stream URL, or the folder.</summary>
    public static string? OpenTarget(string? uri)
    {
        if (uri == null) return null;
        if (KindOf(uri) == MusicKind.Spotify)
        {
            var p = uri.Split(':');
            return p.Length == 3 ? $"https://open.spotify.com/{p[1]}/{p[2]}" : null;
        }
        return KindOf(uri) == null ? null : Target(uri);
    }
}
