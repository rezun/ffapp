using Avalonia;
using FFApp.Infrastructure.SingleInstance;

namespace FFApp;

public static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        var coordinator = SingleInstanceCoordinator.CreateDefault();
        if (!coordinator.TryAcquire())
        {
            try
            {
                await coordinator.SendActivationAsync(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to activate existing FFApp instance: {ex.Message}");
            }
            await coordinator.DisposeAsync();
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
