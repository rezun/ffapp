namespace FFApp.Core.Models;

public sealed record Resolution(int Width, int Height);

public sealed record TargetFileSizeOptions(
    int TargetSizeMb,
    bool TwoPass = false,
    double MuxingOverheadRatio = 0.015);

public sealed record TargetBitrateOptions(
    int VideoBitrate,
    bool TwoPass = false);

public sealed record AudioOptions(
    AudioMode Mode = AudioMode.Copy,
    AudioCodec? Codec = null,
    int? Bitrate = null,
    bool RemixToStereo = false);

public sealed record StreamHandlingOptions(
    bool RemoveSubtitles = false,
    bool RemoveData = false);

public sealed record OutputSettings(
    OutputContainer Container = OutputContainer.Mp4,
    VideoCodec VideoCodec = VideoCodec.H264,
    EncodingStrategy EncodingStrategy = EncodingStrategy.TargetQuality,
    ResolutionPreset ResolutionPreset = ResolutionPreset.Original,
    VideoEncoderOptions? VideoEncoderOptions = null,
    AudioOptions? AudioOptions = null,
    StreamHandlingOptions? StreamHandling = null,
    TargetFileSizeOptions? TargetFileSize = null,
    TargetBitrateOptions? TargetBitrate = null,
    QualityOptions? TargetQuality = null)
{
    public AudioOptions AudioOptions { get; init; } = AudioOptions ?? new AudioOptions();
    public StreamHandlingOptions StreamHandling { get; init; } = StreamHandling ?? new StreamHandlingOptions();
    public TargetFileSizeOptions TargetFileSize { get; init; } = TargetFileSize ?? new TargetFileSizeOptions(50);
    public TargetBitrateOptions TargetBitrate { get; init; } = TargetBitrate ?? new TargetBitrateOptions(3_000_000);
    public QualityOptions TargetQuality { get; init; } = TargetQuality ?? new H264QualityOptions();
}
