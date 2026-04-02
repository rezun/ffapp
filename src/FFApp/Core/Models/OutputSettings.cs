namespace FFApp.Core.Models;

public sealed record Resolution(int Width, int Height);

public static class FileSizeDefaults
{
    public const double MuxingOverheadRatio = 0.03;
    public const int AbsoluteOverheadBytes = 524_288;
    public const int TargetSizeMb = 50;
}

public sealed record TargetFileSizeOptions(
    int TargetSizeMb = FileSizeDefaults.TargetSizeMb,
    bool TwoPass = false,
    double MuxingOverheadRatio = FileSizeDefaults.MuxingOverheadRatio,
    int AbsoluteOverheadBytes = FileSizeDefaults.AbsoluteOverheadBytes)
{
    public long EffectiveOverheadBits(long totalBudgetBits) =>
        Math.Max((long)AbsoluteOverheadBytes * 8, (long)(totalBudgetBits * MuxingOverheadRatio));
}

public sealed record TargetBitrateOptions(
    int VideoBitrate,
    bool TwoPass = false);

public sealed record ToneMapSettings(
    ToneMapAlgorithm Algorithm = ToneMapAlgorithm.Hable,
    double Desaturation = 2.0,
    int? PeakNits = null);

public sealed record AudioOptions(
    AudioMode Mode = AudioMode.Copy,
    AudioCodec? Codec = null,
    int? Bitrate = null,
    int? AudioChannels = null,
    int? SampleRate = null);

public sealed record StreamHandlingOptions(
    bool RemoveSubtitles = false,
    bool RemoveData = false);

public sealed record OutputSettings(
    OutputContainer Container = OutputContainer.Mp4,
    VideoMode VideoMode = VideoMode.Reencode,
    VideoCodec VideoCodec = VideoCodec.H264,
    EncodingStrategy EncodingStrategy = EncodingStrategy.TargetQuality,
    ResolutionPreset ResolutionPreset = ResolutionPreset.Original,
    FrameRatePreset FrameRatePreset = FrameRatePreset.Original,
    VideoEncoderOptions? VideoEncoderOptions = null,
    AudioOptions? AudioOptions = null,
    StreamHandlingOptions? StreamHandling = null,
    TargetFileSizeOptions? TargetFileSize = null,
    TargetBitrateOptions? TargetBitrate = null,
    QualityOptions? TargetQuality = null,
    HdrMode HdrMode = HdrMode.Preserve,
    ToneMapSettings? ToneMapSettings = null)
{
    public AudioOptions AudioOptions { get; init; } = AudioOptions ?? new AudioOptions();
    public StreamHandlingOptions StreamHandling { get; init; } = StreamHandling ?? new StreamHandlingOptions();
    public TargetFileSizeOptions TargetFileSize { get; init; } = TargetFileSize ?? new TargetFileSizeOptions();
    public TargetBitrateOptions TargetBitrate { get; init; } = TargetBitrate ?? new TargetBitrateOptions(3_000_000);
    public QualityOptions TargetQuality { get; init; } = TargetQuality ?? new H264QualityOptions();
    public ToneMapSettings ToneMapSettings { get; init; } = ToneMapSettings ?? new ToneMapSettings();
}
