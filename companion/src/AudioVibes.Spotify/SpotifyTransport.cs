using Windows.Media.Control;

namespace AudioVibes.Spotify;

/// <summary>Play/pause/next/prev through Windows media session controls (SMTC). No Premium needed.</summary>
public sealed class SpotifyTransport
{
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;

    private async Task<GlobalSystemMediaTransportControlsSession?> SessionAsync()
    {
        _mgr ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return _mgr.GetSessions().FirstOrDefault(s =>
            s.SourceAppUserModelId?.Contains("Spotify", StringComparison.OrdinalIgnoreCase) == true);
    }

    public async Task<bool> TogglePlayPauseAsync() =>
        await SessionAsync() is { } s && await s.TryTogglePlayPauseAsync();

    /// <summary>Pauses Spotify if it's playing (used when the companion's own radio/folder player takes over).</summary>
    public async Task<bool> PauseAsync()
    {
        if (await SessionAsync() is not { } s) return false;
        var info = s.GetPlaybackInfo();
        return info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && await s.TryPauseAsync();
    }

    public async Task<bool> NextAsync() =>
        await SessionAsync() is { } s && await s.TrySkipNextAsync();

    public async Task<bool> PreviousAsync() =>
        await SessionAsync() is { } s && await s.TrySkipPreviousAsync();

    /// <summary>"Artist – Title", or null if Spotify isn't running / has no session.</summary>
    public async Task<string?> NowPlayingAsync()
    {
        try
        {
            if (await SessionAsync() is not { } s) return null;
            var props = await s.TryGetMediaPropertiesAsync();
            if (props == null || string.IsNullOrEmpty(props.Title)) return null;
            return string.IsNullOrEmpty(props.Artist) ? props.Title : $"{props.Artist} – {props.Title}";
        }
        catch { return null; }
    }
}
