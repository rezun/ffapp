using System.Collections.Immutable;
using System.Text.Json.Serialization;

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
    ImmutableArray<int>? SelectedSubtitleStreamIndices = null)
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
    double? Speed = null,
    RemoteProgress? Remote = null);

/// <summary>Phases of a job that runs on the server agent.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RemotePhase>))]
public enum RemotePhase
{
    [JsonStringEnumMemberName("uploading")] Uploading,
    [JsonStringEnumMemberName("waiting")] WaitingForServer,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("downloading")] Downloading,
    [JsonStringEnumMemberName("done")] Done,
}

/// <summary>
/// Progress of a server job beyond encoding. <see cref="Percentage"/> and
/// <see cref="BytesPerSecond"/> describe transfers; the queue fields describe waiting on the server.
/// </summary>
public sealed record RemoteProgress(
    RemotePhase Phase,
    double? Percentage = null,
    double? BytesPerSecond = null,
    int? QueuePosition = null,
    DateTimeOffset? EstimatedStartAt = null,
    bool IsPaused = false,
    string? Notice = null);

public sealed record JobResult(
    JobDefinition Job,
    JobState State,
    ImmutableArray<GeneratedCommand> Commands,
    ImmutableArray<ProcessExecutionResult> ProcessResults,
    double ElapsedSeconds,
    string LogOutput);
