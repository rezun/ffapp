using System.Collections.Frozen;
using System.Collections.Immutable;
using FFApp.Core.Models;

namespace FFApp.Core.Validation;

public static class CompatibilityData
{
    public static readonly IReadOnlyList<string> ColorPrimariesOptions =
        ["bt709", "bt470m", "bt470bg", "smpte170m", "smpte240m", "film", "bt2020", "smpte431", "smpte432", "ebu3213"];
    public static readonly IReadOnlyList<string> ColorTransferOptions =
        ["bt709", "gamma22", "gamma28", "smpte170m", "smpte240m", "linear", "log", "log_sqrt",
         "iec61966_2_4", "bt1361e", "iec61966_2_1", "bt2020_10", "bt2020_12", "smpte2084", "smpte428", "arib-std-b67"];
    public static readonly IReadOnlyList<string> ColorMatrixOptions =
        ["bt709", "fcc", "bt470bg", "smpte170m", "smpte240m", "bt2020nc", "bt2020c"];
    public static readonly IReadOnlyList<string> ColorRangeOptions = ["tv", "pc"];

    public static readonly IReadOnlyList<string> H26xPresetOptions =
    [
        "ultrafast", "superfast", "veryfast", "faster", "fast",
        "medium", "slow", "slower", "veryslow",
    ];

    public static readonly IReadOnlyList<string> H264ProfileOptions = ["baseline", "main", "high"];
    public static readonly IReadOnlyList<string> H265ProfileOptions = ["main", "main10"];

    public const int VpxSpeedMin = 0;
    public const int VpxSpeedMax = 8;
    public const int VpxTileColumnsMin = 0;
    public const int VpxTileColumnsMax = 6;
    public const int VpxTileRowsMin = 0;
    public const int VpxTileRowsMax = 2;

    public const int Av1PresetMin = 0;
    public const int Av1PresetMax = 13;
    public const int Av1CrfMin = 0;
    public const int Av1CrfMax = 63;
    public const int Av1MaxBitrateKbpsMin = 0;
    public const int Av1MaxBitrateKbpsMax = 1_000_000;

    public static readonly int Av1FilmGrainMin = 0;
    public static readonly int Av1FilmGrainMax = 50;
    public static readonly int Av1FilmGrainDefaultStrength = 8;
    public static readonly IReadOnlyList<int> Av1FilmGrainToggleOptions = [0, 1];

    public static readonly IReadOnlyList<int> Av1VarianceBoostStrengthOptions = [1, 2, 3, 4];
    public static readonly IReadOnlyList<int> Av1VarianceOctileOptions = [1, 2, 3, 4, 5, 6, 7, 8];

    public static readonly IReadOnlyList<int> Av1TuneOptions = [0, 1, 2, 3, 4];
    public static readonly IReadOnlyList<int> Av1ScmOptions = [0, 1, 2, 3];

    public static readonly FrozenDictionary<OutputContainer, IReadOnlyList<VideoCodec>> VideoCodecMatrix =
        new Dictionary<OutputContainer, IReadOnlyList<VideoCodec>>
        {
            [OutputContainer.Mp4] = [VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1, VideoCodec.Vp9],
            [OutputContainer.Mkv] = [VideoCodec.Av1, VideoCodec.H265, VideoCodec.H264, VideoCodec.Vp9],
            [OutputContainer.Webm] = [VideoCodec.Av1, VideoCodec.Vp9],
            [OutputContainer.Ogg] = [VideoCodec.Vp9],
        }.ToFrozenDictionary();

    public static readonly FrozenDictionary<OutputContainer, IReadOnlyList<AudioCodec>> AudioCodecMatrix =
        new Dictionary<OutputContainer, IReadOnlyList<AudioCodec>>
        {
            [OutputContainer.Mp4] = [AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Opus],
            [OutputContainer.Mkv] = [AudioCodec.Aac, AudioCodec.Opus, AudioCodec.Mp3, AudioCodec.Vorbis],
            [OutputContainer.Webm] = [AudioCodec.Opus, AudioCodec.Vorbis],
            [OutputContainer.Ogg] = [AudioCodec.Opus, AudioCodec.Vorbis],
            [OutputContainer.M4a] = [AudioCodec.Aac],
            [OutputContainer.Mka] = [AudioCodec.Opus, AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Vorbis],
            [OutputContainer.OpusFile] = [AudioCodec.Opus],
        }.ToFrozenDictionary();

    // Rates advertised by FFmpeg's aac, libmp3lame and libopus encoders.
    // libvorbis supports a continuous range; offer common rates in the UI.
    public static readonly FrozenDictionary<AudioCodec, IReadOnlyList<int>> AudioSampleRateOptions =
        new Dictionary<AudioCodec, IReadOnlyList<int>>
        {
            [AudioCodec.Aac] = [7350, 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000],
            [AudioCodec.Mp3] = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000],
            [AudioCodec.Opus] = [8000, 12000, 16000, 24000, 48000],
            [AudioCodec.Vorbis] = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000],
        }.ToFrozenDictionary();

    public static readonly int VorbisSampleRateMax = 200_000;

    public static bool IsAudioSampleRateSupported(AudioCodec codec, int sampleRate) => codec switch
    {
        AudioCodec.Aac or AudioCodec.Mp3 or AudioCodec.Opus => AudioSampleRateOptions[codec].Contains(sampleRate),
        AudioCodec.Vorbis => sampleRate > 0 && sampleRate <= VorbisSampleRateMax,
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported audio codec."),
    };

    public static readonly FrozenSet<OutputContainer> AudioOnlyContainers =
        FrozenSet.ToFrozenSet([OutputContainer.M4a, OutputContainer.Mka, OutputContainer.OpusFile]);

    public static readonly IReadOnlyList<OutputContainer> VideoContainers =
        [OutputContainer.Mp4, OutputContainer.Mkv, OutputContainer.Webm, OutputContainer.Ogg];

    public static readonly IReadOnlyList<OutputContainer> AudioOnlyContainerList =
        [OutputContainer.M4a, OutputContainer.Mka, OutputContainer.OpusFile, OutputContainer.Ogg];

    public static readonly FrozenDictionary<ResolutionPreset, Resolution> PresetResolutions =
        new Dictionary<ResolutionPreset, Resolution>
        {
            [ResolutionPreset.P720] = new(1280, 720),
            [ResolutionPreset.P1080] = new(1920, 1080),
            [ResolutionPreset.P1440] = new(2560, 1440),
            [ResolutionPreset.P4K] = new(3840, 2160),
        }.ToFrozenDictionary();

    public static bool IsAudioOnlyContainer(OutputContainer container) =>
        AudioOnlyContainers.Contains(container);

    public static IReadOnlyList<OutputContainer> ContainersForVideoMode(VideoMode videoMode) =>
        videoMode == VideoMode.Remove ? AudioOnlyContainerList : VideoContainers;

    public static IReadOnlyList<VideoCodec> AllowedVideoCodecs(OutputContainer container) =>
        VideoCodecMatrix.TryGetValue(container, out var codecs) ? codecs : [];

    public static IReadOnlyList<AudioCodec> AllowedAudioCodecs(OutputContainer container) =>
        AudioCodecMatrix[container];

    public static long? EstimateAudioBits(
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
            return (long)(audioBitrate.Value * durationSeconds.Value * streams.Length);
        }

        // Copy mode: sum source bitrates.
        long totalBitrate = 0;
        foreach (var stream in streams)
        {
            if (stream.BitRate is null)
                return null;
            totalBitrate += stream.BitRate.Value;
        }
        return (long)(totalBitrate * durationSeconds.Value);
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
            VideoCodec.Vp9 => "VP9",
            VideoCodec.Av1 => "AV1",
            _ => codec.ToString(),
        };

    public static readonly FrozenSet<VideoCodec> HdrCapableCodecs =
        FrozenSet.ToFrozenSet([VideoCodec.H265, VideoCodec.Av1]);

    public static bool SupportsHdrOutput(VideoCodec codec) =>
        HdrCapableCodecs.Contains(codec);

    public const double LibplaceboSaturationMin = 0.0;
    public const double LibplaceboSaturationMax = 16.0;
    public const double LibplaceboContrastRecoveryMin = 0.0;
    public const double LibplaceboContrastRecoveryMax = 3.0;
}
