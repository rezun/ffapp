using System.Reflection;
using System.Runtime.InteropServices;
using NuGet.Versioning;
using Velopack;
using Velopack.Sources;

namespace FFApp.Services;

public sealed class VelopackUpdateSource(UpdateManager manager, GithubSource releaseSource,
    Velopack.Logging.IVelopackLogger updateLogger) : IAppUpdateSource
{
    public static readonly Uri DownloadsUri = new("https://github.com/rezun/ffapp/releases/latest");
    private UpdateInfo? _available;
    private VelopackAsset? _downloaded;

    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (manager.IsInstalled)
        {
            _downloaded = manager.UpdatePendingRestart;
            if (_downloaded is not null)
                return new(_downloaded.Version.ToNormalizedString(), true, true);

            _available = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken);
            return _available is null ? null : new(_available.TargetFullRelease.Version.ToNormalizedString(), true, false);
        }

        // Source/deploy-mac builds have no Velopack installation metadata. Still check
        // their architecture's release feed, then offer the official installer instead.
        var feed = await releaseSource.GetReleaseFeed(updateLogger, "FFApp", GetChannel(), null, null)
            .WaitAsync(cancellationToken);
        var current = SemanticVersion.Parse(typeof(VelopackUpdateSource).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");
        var latest = feed.Assets.Where(a => a.Type == VelopackAssetType.Full && a.Version > current)
            .OrderByDescending(a => a.Version).FirstOrDefault();
        return latest is null ? null : new(latest.Version.ToNormalizedString(), false, false);
    }

    public async Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken = default)
    {
        var update = _available ?? throw new InvalidOperationException("Check for an update before downloading it.");
        await manager.DownloadUpdatesAsync(update, progress, cancellationToken);
        _downloaded = manager.UpdatePendingRestart
            ?? throw new InvalidOperationException("The update download could not be verified. Please try again.");
    }

    public void ApplyAndRestart() => manager.ApplyUpdatesAndRestart(
        _downloaded ?? throw new InvalidOperationException("Download the update before restarting."));

    internal static string GetChannel()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("Updates support x64 and ARM64."),
        };
        return $"ffapp-{platform}-{architecture}";
    }
}
