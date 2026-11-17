using Avalonia;
using FFApp.Infrastructure;
using FFApp.Infrastructure.Logging;
using FFApp.Infrastructure.SingleInstance;
using FFRux.Infrastructure;

namespace FFRux;

public static class Program
{
    // Main must stay synchronous (not async Task) — async Main breaks OLE drag-drop
    // on Windows even with [STAThread]. See AvaloniaUI/Avalonia#12499, #13694.
    [STAThread]
    public static int Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
            WindowsAppUserModel.ApplyToCurrentProcess(WindowsAppUserModel.FFRuxId);

        using var log = AppLogging.CreateLogger(FFRuxPaths.Identity);

        var coordinator = SingleInstanceCoordinator.CreateFor(FFRuxPaths.Identity);
        if (!coordinator.TryAcquire())
        {
            try
            {
                coordinator.SendActivation(args);
            }
            catch (Exception ex)
            {
                log.Error(ex, "Failed to activate the existing FFRux instance");
            }
            return 0;
        }

        AppLogging.LogUnhandledExceptions(log);
        AppLogging.LogStartup(log, FFRuxPaths.Identity);

        AppBuilder.Configure(() => new App(coordinator, log))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);

        log.Information("FFRux exited");
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
