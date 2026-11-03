using System.Runtime.InteropServices;

namespace FFApp.Infrastructure;

public static class AppPaths
{
    public static string AppDataDirectory(AppIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Path.Combine(PlatformDataRoot(), identity.DataDirectoryName);
    }

    /// <summary>
    /// Where the app writes its logs: <c>~/Library/Logs/&lt;Name&gt;</c> on macOS (Console.app lists it),
    /// <c>%LOCALAPPDATA%\&lt;Name&gt;\Logs</c> on Windows, and <c>$XDG_STATE_HOME/&lt;folder&gt;/logs</c> elsewhere.
    /// </summary>
    public static string LogDirectory(AppIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, identity.Name, "Logs");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Path.Combine(home, "Library", "Logs", identity.Name);

        // Linux / other Unix: logs are state, not configuration (XDG Base Directory spec)
        var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrEmpty(xdgState))
            xdgState = Path.Combine(home, ".local", "state");

        return Path.Combine(xdgState, identity.DataDirectoryName, "logs");
    }

    private static string PlatformDataRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support");
        }

        // Linux / other Unix
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdgConfig))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            xdgConfig = Path.Combine(home, ".config");
        }

        return xdgConfig;
    }
}
