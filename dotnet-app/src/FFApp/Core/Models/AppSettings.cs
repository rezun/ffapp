using System.Collections.Immutable;

namespace FFApp.Core.Models;

public sealed record ExecutablePaths(
    string? Ffmpeg = null,
    string? Ffprobe = null)
{
    public static ExecutablePaths Empty => new();
}

public sealed record ExecutableValidation(
    string Name,
    string? Path,
    bool IsValid,
    string? VersionOutput = null,
    string? Error = null);

public sealed record DependencyStatus(
    ExecutableValidation Ffmpeg,
    ExecutableValidation Ffprobe)
{
    public bool IsReady => Ffmpeg.IsValid && Ffprobe.IsValid;

    public ExecutablePaths ExecutablePaths => new(
        Ffmpeg: Ffmpeg.IsValid ? Ffmpeg.Path : null,
        Ffprobe: Ffprobe.IsValid ? Ffprobe.Path : null);
}

public sealed record SplitterLayout
{
    public ImmutableArray<int> MainSplitterSizes { get; init; } = [];
    public ImmutableArray<int> RightSplitterSizes { get; init; } = [];
    public ImmutableArray<int> QueueSplitterSizes { get; init; } = [];
}

public sealed record WindowGeometry(
    int? X = null,
    int? Y = null,
    int? Width = null,
    int? Height = null,
    bool IsMaximized = false);

public sealed record AppSettings
{
    public ExecutablePaths ExecutablePaths { get; init; } = ExecutablePaths.Empty;
    public OutputSettings LastUsedSettings { get; init; } = new();
    public SplitterLayout SplitterLayout { get; init; } = new();
    public WindowGeometry WindowGeometry { get; init; } = new();
    public int SchemaVersion { get; init; } = 4;
}
