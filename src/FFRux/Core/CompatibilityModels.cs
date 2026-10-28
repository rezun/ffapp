using FFRux.Core.Models;

namespace FFRux.Core;

/// <summary>What the job does with an enabled track, from harmless to blocking.</summary>
public enum CompatibilityLevel
{
    Copy,
    Convert,
    Warning,
    Incompatible,
}

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
}

/// <param name="StatusText">The short status shown in the track table, such as "Copy" or "Not supported in MP4".</param>
/// <param name="Detail">One or more sentences that explain the status in the properties pane.</param>
/// <param name="ConvertToCodec">The FFmpeg subtitle encoder when the track is converted.</param>
public sealed record TrackCompatibility(
    CompatibilityLevel Level,
    string StatusText,
    string Detail,
    string? ConvertToCodec = null);

public sealed record CompatibilityIssue(IssueSeverity Severity, string Message, TrackKey? Track = null);

public sealed record CompatibilityReport(
    OutputFormat Format,
    IReadOnlyDictionary<TrackKey, TrackCompatibility> Tracks,
    IReadOnlyList<CompatibilityIssue> Issues)
{
    public int ErrorCount => Issues.Count(issue => issue.Severity == IssueSeverity.Error);
    public int WarningCount => Issues.Count(issue => issue.Severity == IssueSeverity.Warning);
    public bool CanStart => ErrorCount == 0;

    public TrackCompatibility? For(TrackKey key) => Tracks.GetValueOrDefault(key);
}
