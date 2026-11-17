using System.Collections.Immutable;
using System.Text.Json.Serialization;

using FFApp.Core.Models;

namespace FFApp.Remote.Contracts;

public sealed record ErrorResponse(string Code, string Message);

public sealed record HealthResponse(string Status, string AgentVersion);

/// <summary>Response of <c>GET /info</c>: what the agent runs and what it can encode.</summary>
public sealed record AgentInfo(
    string AgentVersion,
    int ProtocolVersion,
    string ClientName,
    string? FfmpegVersion,
    FfmpegCapabilities? Capabilities,
    bool IsLibplaceboAvailable,
    bool HasMkvpropedit,
    long FreeBytes,
    long QuotaBytes,
    long UsedBytes,
    ImmutableArray<string> OutputRoots,
    ImmutableArray<string> Warnings);

// --- Sources ---

public sealed record CreateSourceRequest(string Fingerprint, long Size, string FileName);

public sealed record SourceStatus(string SourceId, long Size, long ReceivedBytes, bool Complete);

public sealed record CompleteSourceRequest(string Sha256);

// --- Jobs ---

[JsonConverter(typeof(JsonStringEnumConverter<ResultMode>))]
public enum ResultMode
{
    /// <summary>The app downloads the result and acknowledges it; the agent then deletes it.</summary>
    [JsonStringEnumMemberName("download")] Download,

    /// <summary>The result is written to one of the agent's output roots and stays there.</summary>
    [JsonStringEnumMemberName("keep_on_server")] KeepOnServer,
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentJobState>))]
public enum AgentJobState
{
    [JsonStringEnumMemberName("queued")] Queued,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("paused")] Paused,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("canceled")] Canceled,
}

/// <summary>
/// Request of <c>POST /jobs</c>. <see cref="ClientJobId"/> makes the request idempotent.
/// The agent replaces the paths in <see cref="Job"/> with its own; <see cref="OutputFileName"/>
/// must be a bare file name.
/// </summary>
public sealed record CreateJobRequest(
    string ClientJobId,
    string SourceId,
    JobDefinition Job,
    SourceMediaInfo MediaInfo,
    ResultMode ResultMode,
    string OutputFileName,
    string? ServerOutputRoot = null);

public sealed record AgentJob(
    string JobId,
    string ClientJobId,
    string SourceId,
    AgentJobState State,
    ResultMode ResultMode,
    string OutputFileName,
    string? ServerOutputRoot,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    JobProgress? Progress = null,
    string? FailureMessage = null,
    long? OutputSizeBytes = null,
    string? ServerOutputPath = null,
    int? QueuePosition = null,
    int? JobsAhead = null,
    DateTimeOffset? EstimatedStartAt = null,
    bool Acknowledged = false)
{
    [JsonIgnore]
    public bool IsFinished => State is AgentJobState.Completed or AgentJobState.Failed or AgentJobState.Canceled;
}

// --- Queue and load ---

public sealed record RunningJobInfo(bool Own, double? ProgressPercent, DateTimeOffset? EstimatedEndAt);

/// <summary>Anonymous load information. Never reveals other clients' file names, settings or names.</summary>
public sealed record QueueState(
    RunningJobInfo? Running,
    int WaitingJobs,
    int OwnWaitingJobs,
    DateTimeOffset? EstimatedIdleAt);

// --- Events ---

public sealed record JobProgressEvent(string JobId, JobProgress Progress);

public sealed record JobLogEvent(string JobId, string Line);

// --- Updates ---

[JsonConverter(typeof(JsonStringEnumConverter<UpdateStateKind>))]
public enum UpdateStateKind
{
    [JsonStringEnumMemberName("idle")] Idle,
    [JsonStringEnumMemberName("waiting-for-jobs")] WaitingForJobs,
    [JsonStringEnumMemberName("downloading")] Downloading,
    [JsonStringEnumMemberName("restarting")] Restarting,
    [JsonStringEnumMemberName("failed")] Failed,
}

public sealed record UpdateRequest(string Version);

public sealed record UpdateStatus(UpdateStateKind State, string? TargetVersion = null, string? Message = null);
