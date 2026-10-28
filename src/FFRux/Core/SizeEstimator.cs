using System.Globalization;
using FFRux.Core.Models;

namespace FFRux.Core;

/// <param name="Bytes">The sum of all tracks whose size is known.</param>
/// <param name="IsComplete">False when at least one enabled track has no size information.</param>
public readonly record struct SizeEstimate(long Bytes, bool IsComplete);

/// <summary>Estimates the output size from track sizes. A remux adds only a little container overhead.</summary>
public static class SizeEstimator
{
    public static SizeEstimate Estimate(RemuxPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        long total = 0;
        var complete = true;
        foreach (var source in plan.Sources)
        {
            var tracks = plan.Tracks.Where(track => track.Source.SourceNumber == source.Number).ToList();
            var enabled = tracks.Where(track => track.Enabled).ToList();
            if (enabled.Count == 0)
                continue;

            var sizes = enabled.Select(track => TrackBytes(track.Source, source)).ToList();
            if (sizes.All(size => size is not null))
                total += sizes.Sum(size => size ?? 0);
            // All tracks of the file are kept, so the file size is the best guess for the unknown ones.
            else if (enabled.Count == tracks.Count && source.MediaInfo.SizeBytes is { } fileSize)
                total += fileSize;
            else
            {
                total += sizes.Sum(size => size ?? 0);
                complete = false;
            }
        }
        return new SizeEstimate(total, complete);
    }

    internal static long? TrackBytes(SourceTrack track, RemuxSource source)
    {
        if (SourceTracks.TagNumber(track.Details, "NUMBER_OF_BYTES") is { } tagged)
            return tagged;

        var info = source.MediaInfo;
        var duration = track.Details.DurationSeconds ?? DurationTag(track) ?? info.DurationSeconds;
        if (SourceTracks.BitRate(track) is { } bitRate && duration is { } seconds)
            return (long)(bitRate * seconds / 8);

        // A file with a single track (an external audio or subtitle file) is that track.
        if (info.StreamCount == 1 && info.SizeBytes is { } size)
            return size;
        return null;
    }

    private static double? DurationTag(SourceTrack track)
    {
        foreach (var (key, value) in track.Details.Tags)
        {
            if (key.StartsWith("DURATION", StringComparison.OrdinalIgnoreCase)
                && TimeSpan.TryParse(value.Length > 16 ? value[..16] : value, CultureInfo.InvariantCulture, out var duration))
            {
                return duration.TotalSeconds;
            }
        }
        return null;
    }

    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
    };
}
