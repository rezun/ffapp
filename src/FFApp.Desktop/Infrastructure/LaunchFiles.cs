namespace FFApp.Infrastructure;

/// <summary>
/// Turns paths handed to the app at launch (argv, Apple Events, single-instance forwarding)
/// into the existing user files or folders the app should open.
/// </summary>
public static class LaunchFiles
{
    // On macOS, NSApplication inspects argv at launch and dispatches anything that looks like
    // a path to application:openFiles: automatically (legacy Cocoa behavior). When the process
    // is started via `dotnet <App>.dll` — which is what `dotnet run` produces — that puts the
    // managed assembly path on argv, and it arrives in FileActivatedEventArgs alongside any
    // real user file. Runtime files inside our own directory are never legitimate user input,
    // so we reject them regardless of which channel they arrived on.
    private static readonly string RuntimeDirectory =
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(AppContext.BaseDirectory))
        + Path.DirectorySeparatorChar;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static IReadOnlyList<string> FilterExistingPaths(IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var paths = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                continue;
            if (IsInsideRuntimeDirectory(candidate))
                continue;
            paths.Add(candidate);
        }
        return paths;
    }

    internal static bool IsInsideRuntimeDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(RuntimeDirectory, PathComparison);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
