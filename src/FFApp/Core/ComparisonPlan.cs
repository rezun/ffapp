using System.Collections.Immutable;
using FFApp.Core.Models;
using FFApp.Helpers;

namespace FFApp.Core;

public static class ComparisonPlan
{
    public const int DefaultSampleCount = 8;
    public const double DefaultSampleDuration = 12;
    public const double PreRollSeconds = 2;
    public const int MaximumSampleCount = 24;

    public static ImmutableArray<ComparisonSample> Distribute(double start, double end,
        int count = DefaultSampleCount, double duration = DefaultSampleDuration)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start)
            throw new ArgumentOutOfRangeException(nameof(end), "A finite, positive source range is required.");
        if (!double.IsFinite(duration) || duration <= 0)
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (count is < 1 or > MaximumSampleCount)
            throw new ArgumentOutOfRangeException(nameof(count));

        duration = Math.Min(duration, end - start);
        count = Math.Min(count, Math.Max(1, (int)Math.Min(MaximumSampleCount, Math.Floor((end - start) / duration))));
        var stride = (end - start) / count;
        return [.. Enumerable.Range(0, count).Select(i => new ComparisonSample(
            Guid.NewGuid().ToString("N"), $"Sample {i + 1}",
            start + stride * (i + 0.5) - duration / 2, duration))];
    }

    public static bool IsValid(ComparisonSample sample, double start, double end) =>
        double.IsFinite(sample.StartSeconds) && double.IsFinite(sample.DurationSeconds)
        && sample.DurationSeconds > 0 && sample.StartSeconds >= start
        && sample.EndSeconds <= end + 0.000001;

    public static JobDefinition BuildJob(ComparisonSession session, SourceMediaInfo media,
        ComparisonSample sample, ComparisonVariant variant, string outputPath)
    {
        if (!IsValid(sample, session.RangeStart, session.RangeEnd))
            throw new InvalidOperationException("The sample must be inside the comparison range.");
        if (variant.Settings.VideoMode != VideoMode.Reencode)
            throw new InvalidOperationException("Comparison variants must re-encode video.");

        var settings = VideoOnlySettings(variant.Settings);
        // A size target describes the complete movie. Resolve its video bitrate against
        // that duration and its original audio budget before removing audio for the test.
        if (settings.EncodingStrategy == EncodingStrategy.TargetFileSize)
        {
            var fullJob = FormatHelpers.BuildJobDefinition(session.SourcePath, outputPath,
                variant.Settings, session.RangeStart, session.RangeEnd, session.Crop, session.SelectedAudioStreamIndices);
            var bitrate = DefaultCommandBuilder.ResolveVideoBitrate(fullJob, media);
            settings = settings with
            {
                EncodingStrategy = EncodingStrategy.TargetBitrate,
                TargetBitrate = new TargetBitrateOptions(bitrate, settings.TargetFileSize.TwoPass),
            };
        }

        return FormatHelpers.BuildJobDefinition(session.SourcePath, outputPath, settings,
            Math.Max(session.RangeStart, sample.StartSeconds - PreRollSeconds),
            sample.EndSeconds, session.Crop);
    }

    public static OutputSettings VideoOnlySettings(OutputSettings settings) => settings with
    {
        AudioOptions = new AudioOptions(Mode: AudioMode.Remove),
        StreamHandling = new StreamHandlingOptions(RemoveSubtitles: true, RemoveData: true, RemoveImages: true),
        PostProcessing = new PostProcessingOptions(),
    };
}
