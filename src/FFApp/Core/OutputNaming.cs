namespace FFApp.Core;

/// <summary>
/// Naming rules shared by the output preview, single and batch job creation, and renaming
/// queued jobs, so every path produces the same file name for the same source.
/// </summary>
public static class OutputNaming
{
    /// <summary>Path comparer that matches the platform's usual file system case rules.</summary>
    public static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Builds the automatic output file name without extension: the source name, followed by
    /// the preset name when a saved preset is selected.
    /// </summary>
    public static string AutoStem(string sourcePath, string? presetName)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        var sourceStem = Path.GetFileNameWithoutExtension(sourcePath);
        return string.IsNullOrWhiteSpace(presetName)
            ? sourceStem
            : string.Concat(sourceStem, " - ", presetName);
    }

    /// <summary>
    /// Returns true when <paramref name="stem"/> can be used as a file name without extension:
    /// not blank, no surrounding whitespace, no path separators or invalid characters.
    /// </summary>
    public static bool IsValidStem(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        return !string.IsNullOrWhiteSpace(stem)
            && string.Equals(stem, stem.Trim(), StringComparison.Ordinal)
            && stem is not ("." or "..")
            && stem.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && stem.IndexOfAny(['/', '\\']) < 0;
    }
}
