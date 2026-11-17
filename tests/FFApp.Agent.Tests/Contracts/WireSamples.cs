using System.Collections.Immutable;

using FFApp.Core.Models;

namespace FFApp.Agent.Tests.Contracts;

/// <summary>
/// Jobs and media descriptions where every property differs from its default, so a property that
/// does not survive the trip to the agent shows up in the round-trip tests.
/// </summary>
internal static class WireSamples
{
    public static JobDefinition Job(VideoEncoderOptions encoderOptions, QualityOptions quality) => new(
        SourcePath: "/Users/me/Movies/Source (1).mkv",
        OutputPath: "/Users/me/Movies/Source (1) - AV1.mkv",
        Container: OutputContainer.Mkv,
        VideoMode: VideoMode.Copy,
        VideoCodec: VideoCodec.H265,
        EncodingStrategy: EncodingStrategy.TargetBitrate,
        VideoEncoderOptions: encoderOptions,
        AudioOptions: new AudioOptions(AudioMode.Reencode, AudioCodec.Opus, 160, 6, 44_100, true, "loudnorm"),
        StreamHandling: new StreamHandlingOptions(true, true, true),
        ResolutionPreset: ResolutionPreset.P1080,
        FrameRatePreset: FrameRatePreset.Fps25,
        TargetFileSize: new TargetFileSizeOptions(700, true, 0.05, 1_000_000),
        TargetBitrate: new TargetBitrateOptions(4_500_000, true),
        TargetQuality: quality,
        TrimStartSeconds: 12.5,
        TrimEndSeconds: 3_600.25,
        Crop: new CropRect(4, 8, 1912, 800),
        HdrMode: HdrMode.TonemapToSdr,
        ToneMapSettings: new ToneMapSettings(ToneMapEngine.Libplacebo, ToneMapAlgorithm.Reinhard, 1.5, 400,
            LibplaceboToneMapAlgorithm.Spline, GamutMode.Relative, 0.9, 0.5, false, 0.7),
        KeyframeInterval: 240,
        KeyframeUnit: KeyframeUnit.Seconds,
        PostProcessing: new PostProcessingOptions(true),
        SelectedAudioStreamIndices: [1, 3],
        ColorMetadata: new ColorMetadataSettings(ColorMetadataMode.Custom, "bt2020", "smpte2084", "bt2020nc", "pc"),
        SelectedSubtitleStreamIndices: [4]);

    public static TheoryData<VideoEncoderOptions, QualityOptions> EncoderVariants => new()
    {
        { new H26xEncoderOptions(VideoCodec.H265, "slow", "main10", "aq-mode=3"), new H26xQualityOptions(VideoCodec.H265, 20, "slower", "main10") },
        { new VpxEncoderOptions(VideoCodec.Vp9, 4, 2, 1, "-row-mt 1"), new Vp9QualityOptions(28, true) },
        { new Av1EncoderOptions(4, 1, 0, 1, 1, "lp=4", 8, 1, 0, 3, 6), new Av1QualityOptions(24, 4, 1, 0, 1, 8_000, 1, 8, 1, 0, 3, 6) },
    };

    public static SourceMediaInfo Media() => new()
    {
        SourcePath = "/Users/me/Movies/Source (1).mkv",
        ContainerFormat = "matroska,webm",
        DurationSeconds = 5_400.04,
        BitRate = 12_000_000,
        SizeBytes = 8_100_000_000,
        Title = "Source",
        Chapters = [new ChapterInfo(12, 600, "Opening")],
        MetadataKeys = ["title", "encoder"],
        VideoStreams =
        [
            new VideoStreamInfo(2, "hevc", 11_000_000, 3840, 2160, 23.976, 129_000, 90, "yuv420p10le",
                "bt2020", "smpte2084", "bt2020nc", "tv", 10,
                new HdrMetadata(1000, 400, new MasteringDisplayMetadata(
                    "34000", "16000", "13250", "34500", "7500", "3000", "15635", "16450", "50", "10000000")))
            {
                MetadataKeys = ["BPS"],
                Details = Details(),
                HasDolbyVision = true,
            },
        ],
        AttachedPictureStreams = [new AttachedPictureStreamInfo(5, "mjpeg", "cover.jpg", "image/jpeg")],
        AudioStreams =
        [
            new AudioStreamInfo(1, "truehd", 4_000_000, 8, 48_000, "eng")
            {
                ChannelLayout = "7.1",
                MetadataKeys = ["DURATION"],
                Details = Details(),
            },
        ],
        SubtitleStreams = [new SubtitleStreamInfo(4, "subrip", "ger") { Details = Details() }],
        DataStreams = [new DataStreamInfo(6, "ttf") { IsAttachment = true, FileName = "font.ttf", MimeType = "font/ttf" }],
    };

    private static StreamDetails Details() => new()
    {
        Title = "Main",
        Language = "eng",
        Disposition = new StreamDisposition
        {
            Default = true, Forced = true, HearingImpaired = true, VisualImpaired = true, Comment = true, Original = true,
        },
        Profile = "Main 10",
        BitsPerRawSample = 10,
        DurationSeconds = 5_400.04,
        Tags = ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase,
            [KeyValuePair.Create("HANDLER_NAME", "Video"), KeyValuePair.Create("language", "eng")]),
    };
}
