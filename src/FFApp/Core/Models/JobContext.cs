namespace FFApp.Core.Models;

/// <summary>
/// Aggregates all per-job state into a single immutable snapshot.
/// Built by the workspace coordinator and distributed to consumers.
/// </summary>
public sealed record JobContext(
    SourceMediaInfo MediaInfo,
    OutputSettings Settings,
    double? TrimStartSeconds = null,
    double? TrimEndSeconds = null,
    CropRect? Crop = null,
    string? OutputDirectory = null,
    string? OutputFilename = null,
    bool IsAutoName = false)
{
    /// <summary>
    /// Effective duration in seconds accounting for trim points.
    /// Returns null when no trim is active (use source duration).
    /// </summary>
    public double? EffectiveDurationSeconds { get; } = ComputeEffectiveDuration(
        MediaInfo.DurationSeconds, TrimStartSeconds, TrimEndSeconds);

    private static double? ComputeEffectiveDuration(
        double? sourceDuration, double? trimStart, double? trimEnd)
    {
        if (sourceDuration is not > 0)
            return null;
        if (trimStart is null && trimEnd is null)
            return null;

        var start = trimStart ?? 0;
        var end = trimEnd ?? sourceDuration.Value;
        var duration = end - start;
        return duration > 0 ? duration : null;
    }
}
