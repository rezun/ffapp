namespace FFApp.Remote.Contracts;

/// <summary>Version of the app ↔ agent contract. App and agent must report the same value.</summary>
public static class Protocol
{
    /// <summary>Raised whenever a request, response or event changes incompatibly.</summary>
    public static readonly int Version = 1;

    public static readonly int DefaultPort = 8737;

    public static readonly string ApiPrefix = "/api/v1";

    /// <summary>MIME type of server-sent events.</summary>
    public static readonly string EventStreamMediaType = "text/event-stream";
}

/// <summary>Names of the server-sent events published on <c>GET /events</c>.</summary>
public static class AgentEventTypes
{
    public static readonly string JobState = "job-state";
    public static readonly string JobProgress = "job-progress";
    public static readonly string JobLog = "job-log";
    public static readonly string SourceProgress = "source-progress";
    public static readonly string QueueState = "queue-state";
    public static readonly string UpdateState = "update-state";
}

/// <summary>Machine-readable error codes returned in <see cref="ErrorResponse.Code"/>.</summary>
public static class ErrorCodes
{
    public static readonly string NotFound = "not_found";
    public static readonly string InvalidRequest = "invalid_request";
    public static readonly string QuotaExceeded = "quota_exceeded";
    public static readonly string DiskFull = "disk_full";
    public static readonly string OffsetMismatch = "offset_mismatch";
    public static readonly string HashMismatch = "hash_mismatch";
    public static readonly string SourceIncomplete = "source_incomplete";
    public static readonly string InvalidState = "invalid_state";
    public static readonly string UnsupportedJob = "unsupported_job";
    public static readonly string UpdateUnavailable = "update_unavailable";
}
