using System.Collections.Frozen;
using FFApp.Core.Models;

namespace FFApp.Core.Validation;

internal enum SubtitleStreamMode
{
    None,
    Remove,
    Copy,
    Convert,
    Unsupported,
}

internal sealed record SubtitleStreamPolicy(
    SubtitleStreamMode Mode,
    string? EncoderName = null,
    SubtitleStreamInfo? UnsupportedStream = null);

internal static class StreamCompatibilityPolicy
{
    private static readonly FrozenSet<string> KnownAudioCopyCodecNames =
        FrozenSet.ToFrozenSet(
            [
                "aac",
                "ac3",
                "alac",
                "eac3",
                "mp3",
                "opus",
                "vorbis",
            ],
            StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<OutputContainer, FrozenSet<string>> AudioCopyCodecNamesByContainer =
        new Dictionary<OutputContainer, FrozenSet<string>>
        {
            [OutputContainer.Mp4] = AudioSet(["aac", "ac3", "alac", "eac3", "mp3", "opus"]),
            [OutputContainer.M4a] = AudioSet(["aac", "alac"]),
            [OutputContainer.Webm] = AudioSet(["opus", "vorbis"]),
            [OutputContainer.Ogg] = AudioSet(["opus", "vorbis"]),
            [OutputContainer.OpusFile] = AudioSet(["opus"]),
        }.ToFrozenDictionary();

    private static readonly FrozenSet<string> Mp4NativeSubtitleCodecNames =
        FrozenSet.ToFrozenSet(["mov_text", "tx3g"], StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> WebmNativeSubtitleCodecNames =
        FrozenSet.ToFrozenSet(["webvtt"], StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> TextSubtitleCodecNames =
        FrozenSet.ToFrozenSet(
            [
                "ass",
                "mov_text",
                "ssa",
                "srt",
                "subrip",
                "text",
                "tx3g",
                "webvtt",
            ],
            StringComparer.OrdinalIgnoreCase);

    internal static bool IsKnownAudioCopyCodecName(string? codecName) =>
        !string.IsNullOrWhiteSpace(codecName)
        && KnownAudioCopyCodecNames.Contains(codecName);

    internal static bool CanCopyAudioCodec(OutputContainer container, string? codecName)
    {
        if (IsMatroskaContainer(container))
            return true;

        return !string.IsNullOrWhiteSpace(codecName)
            && AudioCopyCodecNamesByContainer.TryGetValue(container, out var codecNames)
            && codecNames.Contains(codecName);
    }

    internal static SubtitleStreamPolicy ResolveSubtitlePolicy(
        OutputContainer container,
        IReadOnlyList<SubtitleStreamInfo> streams,
        bool removeSubtitles)
    {
        if (removeSubtitles)
            return new SubtitleStreamPolicy(SubtitleStreamMode.Remove);

        if (streams.Count == 0)
            return new SubtitleStreamPolicy(SubtitleStreamMode.None);

        if (IsMatroskaContainer(container))
            return new SubtitleStreamPolicy(SubtitleStreamMode.Copy);

        return container switch
        {
            OutputContainer.Mp4 => ResolveTextSubtitlePolicy(streams, Mp4NativeSubtitleCodecNames, "mov_text"),
            OutputContainer.Webm => ResolveTextSubtitlePolicy(streams, WebmNativeSubtitleCodecNames, "webvtt"),
            _ => new SubtitleStreamPolicy(SubtitleStreamMode.Unsupported, UnsupportedStream: streams[0]),
        };
    }

    internal static bool ShouldCopyDataStreams(OutputContainer container, bool removeData) =>
        !removeData && IsMatroskaContainer(container);

    internal static bool CanCopyAttachedPicture(OutputContainer container, AttachedPictureStreamInfo stream) =>
        IsMatroskaContainer(container)
        || (container is OutputContainer.Mp4 or OutputContainer.M4a
            && (string.Equals(stream.CodecName, "png", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stream.CodecName, "mjpeg", StringComparison.OrdinalIgnoreCase)));

    internal static bool ShouldWarnDataStreamsNotPreserved(
        OutputContainer container,
        IReadOnlyList<DataStreamInfo> streams,
        bool removeData) =>
        streams.Count > 0 && !removeData && !IsMatroskaContainer(container);

    private static SubtitleStreamPolicy ResolveTextSubtitlePolicy(
        IReadOnlyList<SubtitleStreamInfo> streams,
        FrozenSet<string> nativeCodecNames,
        string encoderName)
    {
        var allNative = true;
        foreach (var stream in streams)
        {
            if (string.IsNullOrWhiteSpace(stream.CodecName)
                || !TextSubtitleCodecNames.Contains(stream.CodecName))
            {
                return new SubtitleStreamPolicy(SubtitleStreamMode.Unsupported, UnsupportedStream: stream);
            }

            if (!nativeCodecNames.Contains(stream.CodecName))
                allNative = false;
        }

        return allNative
            ? new SubtitleStreamPolicy(SubtitleStreamMode.Copy)
            : new SubtitleStreamPolicy(SubtitleStreamMode.Convert, encoderName);
    }

    private static FrozenSet<string> AudioSet(IEnumerable<string> codecNames) =>
        codecNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsMatroskaContainer(OutputContainer container) =>
        container is OutputContainer.Mkv or OutputContainer.Mka;
}
