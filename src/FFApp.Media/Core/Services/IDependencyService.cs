using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IDependencyService
{
    Task<DependencyStatus> DiscoverAsync(ExecutablePaths configured, CancellationToken ct = default);
    Task<DependencyStatus> ValidatePathsAsync(ExecutablePaths paths, CancellationToken ct = default);
}
