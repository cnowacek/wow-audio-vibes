using System.Runtime.CompilerServices;

namespace AudioVibes.App.Tests;

/// <summary>Points the app at an empty temp folder before any test touches AppConfig, so tests never
/// read (or write) the real %APPDATA%\WoW Audio Vibes state of whoever runs them.</summary>
internal static class TestHome
{
    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "audiovibes-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("AUDIOVIBES_HOME", dir);
    }
}
