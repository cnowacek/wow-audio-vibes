using Serilog;

namespace AudioVibes.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Installer/updater hooks (install, update, uninstall) run here and exit; normal starts fall through.
        Velopack.VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => TrayApp.RemoveAutostart())
            .Run();

        using var single = new Mutex(true, @"Local\AudioVibes.Companion", out bool first);
        if (!first) return;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(AppConfig.Dir, "logs", "audiovibes-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error(e.Exception, "UI thread exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "unhandled");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Warning(e.Exception, "unobserved task"); e.SetObserved(); };

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
