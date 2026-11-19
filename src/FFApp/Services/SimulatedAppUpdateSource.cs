#if DEBUG
namespace FFApp.Services;

public sealed class SimulatedAppUpdateSource(TimeProvider timeProvider) : IAppUpdateSource
{
    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), timeProvider, cancellationToken);
        return new("99.0.0 (simulation)", CanInstall: true, IsDownloaded: false);
    }

    public async Task DownloadAsync(IProgress<int> progress, CancellationToken cancellationToken = default)
    {
        for (var percentage = 5; percentage <= 100; percentage += 5)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), timeProvider, cancellationToken);
            progress.Report(percentage);
        }
    }

    public Task SimulateRestartAsync() => Task.Delay(TimeSpan.FromSeconds(1), timeProvider);

    public void ApplyAndRestart() => throw new InvalidOperationException("A simulated update cannot restart FFApp.");
}
#endif
