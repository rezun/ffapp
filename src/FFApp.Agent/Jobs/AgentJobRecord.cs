using FFApp.Core.Models;
using FFApp.Remote.Contracts;

namespace FFApp.Agent.Jobs;

/// <summary>Persisted state of one job (<c>jobs/&lt;id&gt;/job.json</c>).</summary>
public sealed record AgentJobRecord(
    string JobId,
    string ClientId,
    string ClientJobId,
    string SourceId,
    JobDefinition Job,
    SourceMediaInfo MediaInfo,
    ResultMode ResultMode,
    string OutputFileName,
    string? ServerOutputRoot,
    AgentJobState State,
    DateTimeOffset CreatedAt,
    long Sequence,
    double? MediaSeconds,
    int PassCount,
    string ThroughputKind,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    JobProgress? Progress = null,
    string? FailureMessage = null,
    string? OutputPath = null,
    long? OutputSizeBytes = null)
{
    public bool IsActive => State is AgentJobState.Running or AgentJobState.Paused;

    public bool IsFinished => State is AgentJobState.Completed or AgentJobState.Failed or AgentJobState.Canceled;
}

/// <summary>A finished job's throughput, kept to estimate when waiting jobs start.</summary>
public sealed record ThroughputSample(string Kind, double MediaSeconds, int PassCount, double ElapsedSeconds, DateTimeOffset FinishedAt);
