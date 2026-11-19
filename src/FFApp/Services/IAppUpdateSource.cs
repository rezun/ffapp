namespace FFApp.Services;

public sealed record AppUpdate(string Version, bool CanInstall, bool IsDownloaded);

public interface IAppUpdateSource
{
    Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken = default);
    // Velopack downloads to a temporary file and verifies the package before making it available.
    Task DownloadAsync(IProgress<int> progress, CancellationToken cancellationToken = default);
    void ApplyAndRestart();
}
