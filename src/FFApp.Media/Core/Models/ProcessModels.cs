using System.Collections.Immutable;

namespace FFApp.Core.Models;

public sealed record GeneratedCommand(
    string ExecutablePath,
    ImmutableArray<string> Arguments,
    string DisplayString,
    ProcessProtocol Protocol = ProcessProtocol.Ffmpeg)
{
    public ImmutableArray<string> TemporaryFiles { get; init; } = [];
}

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
