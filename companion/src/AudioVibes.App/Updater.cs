using Serilog;
using Velopack;
using Velopack.Sources;

namespace AudioVibes.App;

/// <summary>
/// Updates from GitHub Releases through Velopack. The installer (Setup.exe from scripts/release.ps1) puts the
/// app in %LOCALAPPDATA%\WoWAudioVibes; this class checks for a newer release, downloads it in the
/// background and applies it when the app quits (or on the next start). Builds run from bin\ aren't
/// "installed" and never update.
/// </summary>
public sealed class Updater
{
    /// <summary>Where releases are published. Change this if you fork the project.</summary>
    public const string RepoUrl = "https://github.com/cnowacek/wow-audio-vibes";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly UpdateManager? _mgr;
    private readonly Func<bool> _enabled;
    private System.Threading.Timer? _timer;
    private readonly SemaphoreSlim _busy = new(1, 1);

    /// <summary>A newer version is downloaded and will be installed on quit (or via RestartNow).</summary>
    public UpdateInfo? Ready { get; private set; }
    public event Action<string>? ReadyChanged;   // version text

    public Updater(Func<bool> enabled)
    {
        _enabled = enabled;
        try
        {
            var mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (mgr.IsInstalled) _mgr = mgr;
        }
        catch (Exception e) { Log.Warning(e, "update manager unavailable"); }
    }

    public bool IsInstalled => _mgr != null;
    public string VersionText => _mgr?.CurrentVersion?.ToString()
        ?? (typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "?") + " (dev build)";

    public void Start()
    {
        if (_mgr == null) return;
        _timer = new System.Threading.Timer(_ => { if (_enabled()) _ = CheckAsync(false); }, null, TimeSpan.FromSeconds(45), Interval);
    }

    /// <summary>Checks, downloads if newer. Returns a short message for the user.</summary>
    public async Task<string> CheckAsync(bool manual)
    {
        if (_mgr == null) return "Updates only work in the installed app (Setup.exe from GitHub Releases).";
        if (!await _busy.WaitAsync(0)) return "Already checking for updates…";
        try
        {
            if (Ready != null) return $"Version {Ready.TargetFullRelease.Version} is ready; it installs when you quit.";
            var info = await _mgr.CheckForUpdatesAsync();
            if (info == null) return $"You're on the latest version ({VersionText}).";
            Log.Information("downloading update {Version}", info.TargetFullRelease.Version);
            await _mgr.DownloadUpdatesAsync(info);
            Ready = info;
            var v = info.TargetFullRelease.Version.ToString();
            ReadyChanged?.Invoke(v);
            return $"Version {v} downloaded; it installs when you quit (or choose Restart to update).";
        }
        catch (Exception e)
        {
            Log.Warning(e, "update check failed");
            return manual ? "Couldn't check for updates: " + e.Message : "";
        }
        finally { _busy.Release(); }
    }

    /// <summary>Installs the downloaded update and restarts the app now.</summary>
    public void RestartNow()
    {
        if (_mgr == null || Ready == null) return;
        _mgr.ApplyUpdatesAndRestart(Ready.TargetFullRelease);
    }

    /// <summary>Called on quit: hands the downloaded update to Velopack to install once we've exited.</summary>
    public void ApplyOnExit()
    {
        if (_mgr == null || Ready == null) return;
        try { _mgr.WaitExitThenApplyUpdates(Ready.TargetFullRelease, silent: true, restart: false); }
        catch (Exception e) { Log.Warning(e, "could not schedule update"); }
    }
}
