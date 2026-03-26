using System.Collections.Frozen;
using System.Collections.Immutable;
using FFApp.Core.Models;

namespace FFApp.Core.Validation;

public static class CompatibilityData
{
    public static readonly IReadOnlyList<string> H26xPresetOptions =
    [
        "ultrafast", "superfast", "veryfast", "faster", "fast",
        "medium", "slow", "slower", "veryslow",
    ];

    public static readonly IReadOnlyList<string> H264ProfileOptions = ["baseline", "main", "high"];
    public static readonly IReadOnlyList<string> H265ProfileOptions = ["main", "main10"];

    public const int VpxSpeedMin = 0;
    public const int VpxSpeedMax = 8;

    public static readonly FrozenDictionary<OutputContainer, IReadOnlyList<VideoCodec>> VideoCodecMatrix =
        new Dictionary<OutputContainer, IReadOnlyList<VideoCodec>>
        {
            [OutputContainer.Mp4] = [VideoCodec.H264, VideoCodec.H265],
            [OutputContainer.Mkv] = [VideoCodec.H264, VideoCodec.H265, VideoCodec.Vp8, VideoCodec.Vp9],
            [OutputContainer.Webm] = [VideoCodec.Vp8, VideoCodec.Vp9],
            [OutputContainer.Ogg] = [VideoCodec.Vp8, VideoCodec.Vp9],
        }.ToFrozenDictionary();

    public static readonly FrozenDictionary<OutputContainer, IReadOnlyList<AudioCodec>> AudioCodecMatrix =
        new Dictionary<OutputContainer, IReadOnlyList<AudioCodec>>
        {
            [OutputContainer.Mp4] = [AudioCodec.Aac, AudioCodec.Mp3],
            [OutputContainer.Mkv] = [AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Opus, AudioCodec.Vorbis],
            [OutputContainer.Webm] = [AudioCodec.Opus, AudioCodec.Vorbis],
            [OutputContainer.Ogg] = [AudioCodec.Opus, AudioCodec.Vorbis],
        }.ToFrozenDictionary();

    public static readonly FrozenDictionary<ResolutionPreset, Resolution> PresetResolutions =
        new Dictionary<ResolutionPreset, Resolution>
        {
            [ResolutionPreset.P720] = new(1280, 720),
            [ResolutionPreset.P1080] = new(1920, 1080),
            [ResolutionPreset.P1440] = new(2560, 1440),
            [ResolutionPreset.P4K] = new(3840, 2160),
        }.ToFrozenDictionary();

    internal static readonly FrozenDictionary<string, AudioCodec> AudioCodecNames =
        new Dictionary<string, AudioCodec>
        {
            ["aac"] = AudioCodec.Aac,
            ["libfdk_aac"] = AudioCodec.Aac,
            ["mp3"] = AudioCodec.Mp3,
            ["libmp3lame"] = AudioCodec.Mp3,
            ["opus"] = AudioCodec.Opus,
            ["libopus"] = AudioCodec.Opus,
            ["vorbis"] = AudioCodec.Vorbis,
            ["libvorbis"] = AudioCodec.Vorbis,
        }.ToFrozenDictionary();

    internal static readonly FrozenSet<string> Mp4SubtitleCodecs =
        FrozenSet.ToFrozenSet(["mov_text", "tx3g"]);

    public static IReadOnlyList<VideoCodec> AllowedVideoCodecs(OutputContainer container) =>
        VideoCodecMatrix[container];

    public static IReadOnlyList<AudioCodec> AllowedAudioCodecs(OutputContainer container) =>
        AudioCodecMatrix[container];

    public static int? EstimateAudioBits(
        AudioMode audioMode,
        int? audioBitrate,
        ImmutableArray<AudioStreamInfo> streams,
        double? durationSeconds)
    {
        if (durationSeconds is null)
            return null;

        if (audioMode is AudioMode.Remove || streams.Length == 0)
            return 0;

        if (audioMode is AudioMode.Reencode)
        {
            if (audioBitrate is null)
                return null;
            return (int)(audioBitrate.Value * durationSeconds.Value * streams.Length);
        }

        // Copy mode: sum source bitrates.
        var totalBitrate = 0;
        foreach (var stream in streams)
        {
            if (stream.BitRate is null)
                return null;
            totalBitrate += stream.BitRate.Value;
        }
        return (int)(totalBitrate * durationSeconds.Value);
    }

    public static int? BitrateWarningThreshold(Resolution? resolution)
    {
        if (resolution is null)
            return null;

        var longEdge = Math.Max(resolution.Width, resolution.Height);
        return longEdge switch
        {
            >= 3840 => 12_000_000,
            >= 2560 => 6_000_000,
            >= 1920 => 3_000_000,
            >= 1280 => 1_500_000,
            _ => 800_000,
        };
    }

    public static IReadOnlyList<string> QualityPresetOptions(VideoCodec codec) =>
        codec is VideoCodec.H264 or VideoCodec.H265
            ? H26xPresetOptions
            : [];

    public static IReadOnlyList<string> QualityProfileOptions(VideoCodec codec) =>
        codec switch
        {
            VideoCodec.H264 => H264ProfileOptions,
            VideoCodec.H265 => H265ProfileOptions,
            _ => [],
        };

    public static string VideoCodecLabel(VideoCodec codec) =>
        codec switch
        {
            VideoCodec.H264 => "H.264",
            VideoCodec.H265 => "H.265",
            VideoCodec.Vp8 => "VP8",
            VideoCodec.Vp9 => "VP9",
            _ => codec.ToString(),
        };
}
