using AudioVibes.Decoder;
using AudioVibes.Spotify;
using Serilog;

namespace AudioVibes.App;

/// <summary>Maps decoded commands (direct actions or EVT_* events via config.eventMap) to Spotify actions.</summary>
public sealed class ActionRouter(SpotifyTransport transport, SpotifyVolume volume, SpotifyWebApi web, ZoneMusic zoneMusic, Func<AppConfig> config)
{
    public event Action? PingReceived;
    public event Action<string, bool>? Notify; // message, isError

    private CancellationTokenSource? _autoRevert;

    public async Task HandleAsync(DecodedCommand cmd)
    {
        var cfg = config();
        if (cmd.Name == "PING") { PingReceived?.Invoke(); return; }

        string action = cmd.Name;
        int? slot = null, autoRevert = null;
        if (cfg.ZoneMusic.Enabled && cmd.Name.StartsWith("EVT_ENTER_"))
        {
            zoneMusic.OnCategory(cmd.Name); // paired with the zone id the addon sent just before
            return;
        }
        if (cmd.Name is "MODE_RELAXED" or "MODE_LOCKED")
        {
            zoneMusic.OnMode(cmd.Name == "MODE_LOCKED" ? Modes.Locked : Modes.Relaxed);
            return;
        }
        try
        {
            if (cmd.Name == "PLAY_LOCATION") { await zoneMusic.PlayNowAsync(); return; }   // "Play now" in game
            if (cmd.Name == "EVT_HYPE" && await zoneMusic.PlayHypeAsync()) return;          // in-game Hype playlist
        }
        catch (SpotifyNotConfiguredException e) { Notify?.Invoke(e.Message, true); return; }
        catch (Exception e) { Log.Warning(e, "{Cmd} failed", cmd.Name); Notify?.Invoke($"{cmd.Name} failed: {e.Message}", true); return; }

        if (cmd.Name.StartsWith("EVT_"))
        {
            if (!cfg.EventMap.TryGetValue(cmd.Name, out var ea) && cmd.Name == "EVT_LEVEL_UP")
                ea = new EventAction { Action = "FANFARE" };   // configs from before the fanfare existed
            if (ea == null || ea.Action is "NONE" or "")
            {
                Log.Information("event {Event} has no mapping", cmd.Name);
                return;
            }
            action = ea.Action; slot = ea.Slot; autoRevert = ea.AutoRevertMs;
        }
        else if (cmd.Name.StartsWith("PLAYLIST_SLOT_"))
        {
            slot = int.Parse(cmd.Name["PLAYLIST_SLOT_".Length..]);
            action = "PLAYLIST_SLOT";
        }

        try
        {
            await RunAsync(action, slot, autoRevert, cfg);
        }
        catch (SpotifyNotConfiguredException e) { Notify?.Invoke(e.Message, true); }
        catch (Exception e)
        {
            Log.Warning(e, "action {Action} failed", action);
            Notify?.Invoke($"{action} failed: {e.Message}", true);
        }
    }

    public async Task RunAsync(string action, int? slot, int? autoRevertMs, AppConfig cfg)
    {
        Log.Information("action {Action} slot={Slot}", action, slot);
        bool ok = true;
        switch (action)
        {
            case "PLAY_PAUSE": ok = await transport.TogglePlayPauseAsync(); break;
            case "NEXT": ok = await transport.NextAsync(); break;
            case "PREV": ok = await transport.PreviousAsync(); break;
            case "VOL_UP": ok = volume.Step(+cfg.VolumeStep); break;
            case "VOL_DOWN": ok = volume.Step(-cfg.VolumeStep); break;
            case "DUCK_ON":
                ok = await volume.DuckAsync(cfg.DuckLevel);
                _autoRevert?.Cancel();
                if (autoRevertMs is > 0)
                {
                    var cts = _autoRevert = new CancellationTokenSource();
                    _ = Task.Delay(autoRevertMs.Value, cts.Token).ContinueWith(async t =>
                    {
                        if (!t.IsCanceled) await volume.UnduckAsync();
                    }, TaskScheduler.Default);
                }
                break;
            case "DUCK_OFF":
                _autoRevert?.Cancel();
                ok = await volume.UnduckAsync();
                break;
            case "LIKE_CURRENT":
                var name = await web.LikeCurrentAsync();
                Notify?.Invoke($"♥ saved: {name}", false);
                break;
            case "PLAYLIST_SLOT":
                if (slot is null || !cfg.PlaylistSlots.TryGetValue(slot.Value.ToString(), out var uri) || string.IsNullOrWhiteSpace(uri))
                {
                    Notify?.Invoke($"Playlist slot {slot} is empty in config.json", true);
                    return;
                }
                await web.PlayContextAsync(SpotifyUri.Normalize(uri) ?? uri);
                zoneMusic.ForgetCurrent(); // a manual pick: the next zone change may start its playlist again
                break;
            case "FANFARE":
                _ = zoneMusic.PlayForAWhileAsync("fanfare", TimeSpan.FromSeconds(Math.Max(5, cfg.FanfareSeconds)));
                break;
            default:
                Notify?.Invoke($"Unknown action '{action}' in config.json", true);
                return;
        }
        if (!ok) Log.Information("{Action}: Spotify session not found or command refused", action);
    }
}
