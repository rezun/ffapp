using System.Runtime.InteropServices;

namespace FFApp.Infrastructure;

public static class AppPaths
{
    public const string AppDirectoryName = "ffapp";
    public const string SettingsFileName = "settings.json";
    public const string PresetsFileName = "presets.json";

    public static string AppDataDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, AppDirectoryName);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", AppDirectoryName);
        }

        // Linux / other Unix
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdgConfig))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            xdgConfig = Path.Combine(home, ".config");
        }

        return Path.Combine(xdgConfig, AppDirectoryName);
    }

    public static string SettingsFilePath() =>
        Path.Combine(AppDataDirectory(), SettingsFileName);

    public static string PresetsFilePath() =>
        Path.Combine(AppDataDirectory(), PresetsFileName);
}
