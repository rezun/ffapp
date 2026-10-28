namespace FFApp.Core.Models;

public enum OutputLocationMode
{
    SourceFolder,
    CustomFolder,
}

/// <summary>
/// Where encoded files are written. Stored as an app preference rather than in presets,
/// because a preset describes how to encode, not where the result goes.
/// </summary>
public sealed record OutputLocation(
    OutputLocationMode Mode = OutputLocationMode.SourceFolder,
    string? CustomFolder = null)
{
    /// <summary>
    /// The folder to write to, or null to write next to each source file. A custom mode
    /// without a chosen folder falls back to the source folder.
    /// </summary>
    public string? EffectiveDirectory =>
        Mode == OutputLocationMode.CustomFolder && !string.IsNullOrWhiteSpace(CustomFolder)
            ? CustomFolder
            : null;
}
