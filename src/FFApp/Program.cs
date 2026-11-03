using Avalonia;
using FFApp.Infrastructure;
using FFApp.Infrastructure.Logging;
using FFApp.Infrastructure.SingleInstance;

namespace FFApp;

public static class Program
{
    // Main must stay synchronous (not async Task) — async Main breaks OLE drag-drop
    // on Windows even with [STAThread]. See AvaloniaUI/Avalonia#12499, #13694.
    [STAThread]
    public static int Main(string[] args)
    {
        using var log = AppLogging.CreateLogger(FFAppPaths.Identity);

        var coordinator = SingleInstanceCoordinator.CreateFor(FFAppPaths.Identity);
        if (!coordinator.TryAcquire())
        {
            try
            {
                coordinator.SendActivation(args);
            }
            catch (Exception ex)
            {
                log.Error(ex, "Failed to activate the existing FFApp instance");
            }
            return 0;
        }

        AppLogging.LogUnhandledExceptions(log);
        AppLogging.LogStartup(log, FFAppPaths.Identity);

        AppBuilder.Configure(() => new App(coordinator, log))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);

        log.Information("FFApp exited");
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
