using System.Globalization;

namespace FFApp.Core;

/// <summary>How a container moves its index to the front so playback can start before the whole file has arrived.</summary>
public enum StreamingLayout
{
    None,

    /// <summary>MP4 family: <c>+faststart</c> rewrites the file once after muxing to move the moov atom forward.</summary>
    FastStart,

    /// <summary>Matroska and WebM: the Cues go into space reserved after the header.</summary>
    CuesToFront,
}

public static class StreamingMuxerArguments
{
    // MP4 keeps +faststart: -moov_size has no fallback, and an undersized reservation fails the job after muxing.
    private static readonly IReadOnlyList<string> FastStart = ["-movflags", "+faststart"];
    private static readonly IReadOnlyList<string> CuesToFront = ["-cues_to_front", "1"];

    // FFmpeg writes about 20 bytes per cue point, one per video keyframe or subtitle event. 64 bytes per second covers
    // keyframes every 0.4 s, or a 2 s GOP plus several subtitle tracks, while the unused padding stays below 250 KB per hour.
    private const int ReservedCueBytesPerSecond = 64;
    private const int ReservedCueBaseBytes = 16 * 1024;

    /// <param name="durationSeconds">
    /// The expected output length. Matroska outputs reserve room for the Cues from it, so FFmpeg writes them in place
    /// instead of shifting the whole file after muxing. <c>-cues_to_front</c> stays as the fallback when the Cues outgrow
    /// the reservation.
    /// </param>
    public static IReadOnlyList<string> For(StreamingLayout layout, double? durationSeconds) => layout switch
    {
        StreamingLayout.FastStart => FastStart,
        StreamingLayout.CuesToFront when ReservedCueBytes(durationSeconds) is { } bytes =>
            ["-reserve_index_space", bytes.ToString(CultureInfo.InvariantCulture), .. CuesToFront],
        StreamingLayout.CuesToFront => CuesToFront,
        _ => [],
    };

    private static int? ReservedCueBytes(double? durationSeconds) =>
        durationSeconds is { } seconds && seconds > 0 && double.IsFinite(seconds)
            ? (int)Math.Min(int.MaxValue, ReservedCueBaseBytes + Math.Ceiling(seconds) * ReservedCueBytesPerSecond)
            : null;
}
