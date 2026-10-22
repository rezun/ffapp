using System.Collections.Immutable;

namespace FFApp.Core.Models;

public sealed record ComparisonSample(string Id, string Name, double StartSeconds, double DurationSeconds)
{
    public double EndSeconds => StartSeconds + DurationSeconds;
}

public sealed record ComparisonVariant(string Id, string Name, OutputSettings Settings);

public sealed record ComparisonResult(
    string SampleId, string VariantId, string CacheKey, long SizeBytes, double ElapsedSeconds);

public sealed record ComparisonSession(
    string SourcePath,
    string SourceFingerprint,
    OutputSettings Baseline,
    double RangeStart,
    double RangeEnd,
    CropRect? Crop,
    ImmutableArray<ComparisonSample> Samples,
    ImmutableArray<ComparisonVariant> Variants,
    ImmutableArray<ComparisonResult> Results,
    ImmutableArray<int>? SelectedAudioStreamIndices = null);

public sealed record ComparisonClip(
    string Path, double OffsetSeconds, double DurationSeconds,
    long SizeBytes, double ElapsedSeconds, string CacheKey, bool WasCached);
