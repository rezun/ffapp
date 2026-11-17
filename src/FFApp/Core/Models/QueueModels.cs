using System.Collections.Immutable;

using FFApp.Remote.Contracts;

namespace FFApp.Core.Models;

/// <param name="Remote">Set when the job runs on the server agent instead of this computer.</param>
public sealed record QueuedJob(
    JobDefinition Job,
    SourceMediaInfo MediaInfo,
    ImmutableArray<GeneratedCommand> Commands,
    ValidationResult Validation,
    RemoteTarget? Remote = null)
{
    public bool IsRemote => Remote is not null;
}

/// <summary>
/// Where a server job's result goes. <see cref="Key"/> identifies the job in the
/// <c>RemoteJobStore</c> across app restarts; <see cref="DisplayLabel"/> names it in the queue again.
/// </summary>
public sealed record RemoteTarget(string Key, ResultMode ResultMode, string? ServerOutputRoot = null, string DisplayLabel = "");

public sealed record QueueItem(
    string Id,
    QueuedJob QueuedJob,
    JobState State = JobState.Pending,
    JobProgress? Progress = null,
    JobResult? Result = null,
    string? FailureMessage = null,
    string? LogPath = null);
