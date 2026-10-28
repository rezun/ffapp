using System.Runtime.InteropServices;

namespace FFApp.Infrastructure;

public static class AppPaths
{
    public static string AppDataDirectory(AppIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Path.Combine(PlatformDataRoot(), identity.DataDirectoryName);
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
