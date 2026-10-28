using Avalonia;
using FFApp.Infrastructure;
using FFApp.Infrastructure.SingleInstance;

namespace FFApp;

public static class Program
{
    // Main must stay synchronous (not async Task) — async Main breaks OLE drag-drop
    // on Windows even with [STAThread]. See AvaloniaUI/Avalonia#12499, #13694.
    [STAThread]
    public static int Main(string[] args)
    {
        var coordinator = SingleInstanceCoordinator.CreateFor(FFAppPaths.Identity);
        if (!coordinator.TryAcquire())
        {
            try
            {
                coordinator.SendActivation(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to activate existing FFApp instance: {ex.Message}");
            }
            return 0;
        }

        AppBuilder.Configure(() => new App(coordinator))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
