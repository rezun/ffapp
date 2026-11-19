using System.Collections.Immutable;

namespace FFApp.Core.Models;

public sealed record JobDefinition(
    string SourcePath,
    string OutputPath,
    OutputContainer Container,
    VideoMode VideoMode = VideoMode.Reencode,
    VideoCodec VideoCodec = VideoCodec.H264,
    EncodingStrategy EncodingStrategy = EncodingStrategy.TargetQuality,
    VideoEncoderOptions? VideoEncoderOptions = null,
    AudioOptions? AudioOptions = null,
    StreamHandlingOptions? StreamHandling = null,
    ResolutionPreset ResolutionPreset = ResolutionPreset.Original,
    FrameRatePreset FrameRatePreset = FrameRatePreset.Original,
    TargetFileSizeOptions? TargetFileSize = null,
    TargetBitrateOptions? TargetBitrate = null,
    QualityOptions? TargetQuality = null,
    double? TrimStartSeconds = null,
    double? TrimEndSeconds = null,
    CropRect? Crop = null,
    HdrMode HdrMode = HdrMode.Preserve,
    ToneMapSettings? ToneMapSettings = null,
    int? KeyframeInterval = null,
    KeyframeUnit KeyframeUnit = KeyframeUnit.Frames,
    PostProcessingOptions? PostProcessing = null,
    ImmutableArray<int>? SelectedAudioStreamIndices = null,
    ColorMetadataSettings? ColorMetadata = null,
    ImmutableArray<int>? SelectedSubtitleStreamIndices = null,
    bool OptimizeForStreaming = false)
{
    public ColorMetadataSettings ColorMetadata { get; init; } = ColorMetadata ?? new ColorMetadataSettings();
    public AudioOptions AudioOptions { get; init; } = AudioOptions ?? new AudioOptions();
    public StreamHandlingOptions StreamHandling { get; init; } = StreamHandling ?? new StreamHandlingOptions();
    public ToneMapSettings ToneMapSettings { get; init; } = ToneMapSettings ?? new ToneMapSettings();
    public PostProcessingOptions PostProcessing { get; init; } = PostProcessing ?? new PostProcessingOptions();
}

public sealed record CropRect(int X, int Y, int Width, int Height);

public sealed record JobProgress(
    JobState State,
    int PassIndex,
    int PassCount,
    double ElapsedSeconds,
    double? ProcessedSeconds = null,
    double? Percentage = null,
    double? Speed = null);

public sealed record JobResult(
    JobDefinition Job,
    JobState State,
    ImmutableArray<GeneratedCommand> Commands,
    ImmutableArray<ProcessExecutionResult> ProcessResults,
    double ElapsedSeconds,
    string LogOutput);
