using System.Collections.Immutable;

namespace FFApp.Core.Models;

public sealed record ExecutablePaths(
    string? Ffmpeg = null,
    string? Ffprobe = null,
    string? Mkvpropedit = null)
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
    ExecutableValidation Ffprobe,
    ExecutableValidation Mkvpropedit,
    bool IsLibplaceboAvailable = false)
{
    public bool IsReady => Ffmpeg.IsValid && Ffprobe.IsValid;

    public ExecutablePaths ExecutablePaths => new(
        Ffmpeg: Ffmpeg.IsValid ? Ffmpeg.Path : null,
        Ffprobe: Ffprobe.IsValid ? Ffprobe.Path : null,
        Mkvpropedit: Mkvpropedit.IsValid ? Mkvpropedit.Path : null);
}

public sealed record SplitterLayout
{
    public ImmutableArray<int> MainSplitterSizes { get; init; } = [];
    public ImmutableArray<int> QueueSplitterSizes { get; init; } = [];
    public int PreviewHeight { get; init; }
}

public sealed record WindowGeometry(
    int? X = null,
    int? Y = null,
    int? Width = null,
    int? Height = null,
    bool IsMaximized = false);

public enum ThemePreference
{
    System,
    Dark,
    Light,
}

public sealed record AppSettings
{
    public ExecutablePaths ExecutablePaths { get; init; } = ExecutablePaths.Empty;
    public OutputSettings LastUsedSettings { get; init; } = new();
    public string? LastUsedPresetName { get; init; }
    public SplitterLayout SplitterLayout { get; init; } = new();
    public WindowGeometry WindowGeometry { get; init; } = new();
    public ThemePreference ThemePreference { get; init; } = ThemePreference.System;
    public int SchemaVersion { get; init; } = 4;
}
