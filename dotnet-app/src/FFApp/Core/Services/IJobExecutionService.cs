using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IJobExecutionService
{
    Task<JobResult> ExecuteAsync(
        JobDefinition job,
        SourceMediaInfo mediaInfo,
        Action<JobProgress>? progressCallback = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default);
}
