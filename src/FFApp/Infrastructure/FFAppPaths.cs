namespace FFApp.Infrastructure;

/// <summary>FFApp's identity and the files it keeps in its per-user data directory.</summary>
public static class FFAppPaths
{
    public static readonly AppIdentity Identity = new("FFApp", "ffapp");

    public static readonly string SettingsFileName = "settings.json";
    public static readonly string PresetsFileName = "presets.json";
    public static readonly string ComparisonsDirectoryName = "comparisons";

    public static string AppDataDirectory() => AppPaths.AppDataDirectory(Identity);

    public static string SettingsFilePath() =>
        Path.Combine(AppDataDirectory(), SettingsFileName);

    public static string PresetsFilePath() =>
        Path.Combine(AppDataDirectory(), PresetsFileName);

    public static string ComparisonsDirectory() =>
        Path.Combine(AppDataDirectory(), ComparisonsDirectoryName);
}
