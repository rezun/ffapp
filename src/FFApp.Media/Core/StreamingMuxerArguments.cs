namespace FFApp.Core;

public static class StreamingMuxerArguments
{
    public static IReadOnlyList<string> FastStart { get; } = ["-movflags", "+faststart"];
    public static IReadOnlyList<string> CuesToFront { get; } = ["-cues_to_front", "1"];
}
