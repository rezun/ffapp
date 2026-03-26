using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IDependencyService
{
    Task<DependencyStatus> DiscoverAsync(AppSettings settings, CancellationToken ct = default);
    Task<DependencyStatus> ValidatePathsAsync(ExecutablePaths paths, CancellationToken ct = default);
}
