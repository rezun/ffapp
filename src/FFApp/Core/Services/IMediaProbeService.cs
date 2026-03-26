using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IMediaProbeService
{
    Task<SourceMediaInfo> ProbeAsync(string sourcePath, CancellationToken ct = default);
}
