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
