using System.Collections.Immutable;

namespace FFApp.Core.Models;

public sealed record JobDefinition(
    string SourcePath,
    string OutputPath,
    OutputContainer Container,
    VideoCodec VideoCodec,
    EncodingStrategy EncodingStrategy,
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
    CropRect? Crop = null)
{
    public AudioOptions AudioOptions { get; init; } = AudioOptions ?? new AudioOptions();
    public StreamHandlingOptions StreamHandling { get; init; } = StreamHandling ?? new StreamHandlingOptions();
}

public sealed record CropRect(int X, int Y, int Width, int Height);

public sealed record GeneratedCommand(
    string ExecutablePath,
    ImmutableArray<string> Arguments,
    string DisplayString);

public sealed record ProcessProgress(
    double ElapsedSeconds,
    double? ProcessedSeconds = null,
    double? Percentage = null,
    double? Speed = null);

public sealed record ProcessExecutionResult(
    GeneratedCommand Command,
    JobState State,
    int ReturnCode,
    double ElapsedSeconds,
    string StderrOutput);

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
