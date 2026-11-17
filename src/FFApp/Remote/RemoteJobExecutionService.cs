using FFApp.Core.Models;
using FFApp.Core.Services;

namespace FFApp.Remote;

/// <summary>
/// The queue's execution service for one server job. The queue picks it instead of the local
/// <c>DefaultJobExecutionService</c> when a job targets the server.
/// </summary>
public sealed class RemoteJobExecutionService(RemoteJobExecutor executor, RemoteTarget target) : IJobExecutionService
{
    public Task<JobResult> ExecuteAsync(
        JobDefinition job,
        SourceMediaInfo mediaInfo,
        Action<JobProgress>? progressCallback = null,
        Action<string>? logCallback = null,
        ExecutionPauseState? pauseState = null,
        string? workDirectory = null,
        CancellationToken ct = default) =>
        executor.ExecuteAsync(target, target.DisplayLabel, job, mediaInfo, progressCallback, logCallback, pauseState, ct);
}
