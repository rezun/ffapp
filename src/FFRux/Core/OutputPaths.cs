using FFRux.Core.Models;

namespace FFRux.Core;

public static class OutputPaths
{
    /// <summary>
    /// Next to the first source, with the format's extension. A name that would hit a source gets
    /// " (remux)" so FFRux never writes over its input.
    /// </summary>
    public static string Suggest(string firstSourcePath, OutputFormat format, IReadOnlyCollection<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(firstSourcePath);
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var directory = Path.GetDirectoryName(Path.GetFullPath(firstSourcePath)) ?? "";
        var stem = Path.GetFileNameWithoutExtension(firstSourcePath);
        var extension = OutputFormats.Get(format).Extension;

        var candidate = Path.Combine(directory, stem + extension);
        if (!IsSource(candidate, sourcePaths))
            return candidate;
        return Path.Combine(directory, $"{stem} (remux){extension}");
    }

    /// <summary>Keeps the folder and name the user chose and swaps only the extension.</summary>
    public static string WithFormat(string path, OutputFormat format, IReadOnlyCollection<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var changed = Path.ChangeExtension(path, OutputFormats.Get(format).Extension);
        if (!IsSource(changed, sourcePaths))
            return changed;
        var directory = Path.GetDirectoryName(changed) ?? "";
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(changed)} (remux){Path.GetExtension(changed)}");
    }

    /// <summary>The hidden file FFmpeg writes to; it replaces the output only after the job succeeded.</summary>
    public static string PartialFile(string outputPath)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? "";
        var name = Path.GetFileNameWithoutExtension(outputPath);
        return Path.Combine(directory, $".{name}.ffrux-partial{Path.GetExtension(outputPath)}");
    }

    private static bool IsSource(string candidate, IReadOnlyCollection<string> sourcePaths)
    {
        var full = Path.GetFullPath(candidate);
        return sourcePaths.Any(source => CompatibilityChecker.PathsEqual(Path.GetFullPath(source), full));
    }
}
