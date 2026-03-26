using System.Collections.Immutable;

namespace FFApp.Core.Models;

public sealed record QueuedJob(
    JobDefinition Job,
    SourceMediaInfo MediaInfo,
    ImmutableArray<GeneratedCommand> Commands,
    ValidationResult Validation);

public sealed record QueueItem(
    string Id,
    QueuedJob QueuedJob,
    JobState State = JobState.Pending,
    JobProgress? Progress = null,
    JobResult? Result = null,
    string? FailureMessage = null);
