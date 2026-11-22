using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace AudioVibes.Spotify;

/// <summary>Per-app volume on Spotify.exe through Core Audio sessions (ISimpleAudioVolume). No Premium needed.</summary>
public sealed class SpotifyVolume
{
    private readonly object _gate = new();
    private float? _preDuck;
    private CancellationTokenSource? _ramp;

    public string ProcessName { get; set; } = "Spotify";

    private List<SimpleAudioVolume> Sessions()
    {
        var list = new List<SimpleAudioVolume>();
        using var en = new MMDeviceEnumerator();
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var mgr = dev.AudioSessionManager;
            mgr.RefreshSessions();
            var sessions = mgr.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                try
                {
                    var pid = (int)s.GetProcessID;
                    if (pid == 0) continue;
                    using var p = Process.GetProcessById(pid);
                    if (string.Equals(p.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase))
                        list.Add(s.SimpleAudioVolume);
                }
                catch { /* process gone */ }
            }
        }
        return list;
    }

    /// <summary>Current Spotify session volume 0..1, or null if Spotify has no audio session.</summary>
    public float? Get() => Sessions().Select(s => (float?)s.Volume).FirstOrDefault();

    private bool Set(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        var ss = Sessions();
        foreach (var s in ss) s.Volume = v;
        return ss.Count > 0;
    }

    public bool Step(float delta)
    {
        CancelRamp();
        var cur = Get();
        if (cur is null) return false;
        lock (_gate) if (_preDuck.HasValue) _preDuck = Math.Clamp(_preDuck.Value + delta, 0f, 1f);
        return Set(cur.Value + delta);
    }

    public async Task<bool> DuckAsync(float level, int rampMs = 300)
    {
        var cur = Get();
        if (cur is null) return false;
        lock (_gate) _preDuck ??= cur.Value;   // keep the original level if already ducked
        await RampAsync(cur.Value, Math.Min(level, cur.Value), rampMs);
        return true;
    }

    public async Task<bool> UnduckAsync(int rampMs = 300)
    {
        float? target;
        lock (_gate) { target = _preDuck; _preDuck = null; }
        var cur = Get();
        if (target is null || cur is null) return false;
        await RampAsync(cur.Value, target.Value, rampMs);
        return true;
    }

    public bool IsDucked { get { lock (_gate) return _preDuck.HasValue; } }

    private void CancelRamp()
    {
        lock (_gate) { _ramp?.Cancel(); _ramp = null; }
    }

    private async Task RampAsync(float from, float to, int ms)
    {
        CancellationTokenSource cts;
        lock (_gate) { _ramp?.Cancel(); cts = _ramp = new CancellationTokenSource(); }
        const int steps = 15;
        try
        {
            for (int i = 1; i <= steps; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                Set(from + (to - from) * i / steps);
                await Task.Delay(ms / steps, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
}
