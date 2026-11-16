using System.Collections.Immutable;

namespace FFApp.Core.Models;

public sealed record FfmpegCapabilities(
    ImmutableHashSet<string> Encoders,
    ImmutableHashSet<string> Filters);

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
    bool IsLibplaceboAvailable = false,
    FfmpegCapabilities? Capabilities = null)
{
    public bool IsReady => Ffmpeg.IsValid && Ffprobe.IsValid;

    public string RequiredToolsError => string.Join(Environment.NewLine,
        new[] { Ffmpeg, Ffprobe }.Where(tool => !tool.IsValid)
            .Select(tool => tool.Error ?? $"{tool.Name} is not available."));

    public ExecutablePaths ExecutablePaths => new(
        Ffmpeg: Ffmpeg.IsValid ? Ffmpeg.Path : null,
        Ffprobe: Ffprobe.IsValid ? Ffprobe.Path : null,
        Mkvpropedit: Mkvpropedit.IsValid ? Mkvpropedit.Path : null);
}
