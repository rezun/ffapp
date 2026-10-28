using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public sealed class FFAppPathsTests
{
    // Renaming FFApp's identity would silently orphan every user's settings, presets and comparison cache.
    [Fact]
    public void Identity_KeepsTheExistingDataFolder()
    {
        FFAppPaths.Identity.Should().Be(new AppIdentity("FFApp", "ffapp"));
    }

    [Fact]
    public void Files_LiveInFFAppsDataDirectory()
    {
        var root = AppPaths.AppDataDirectory(FFAppPaths.Identity);

        FFAppPaths.SettingsFilePath().Should().Be(Path.Combine(root, "settings.json"));
        FFAppPaths.PresetsFilePath().Should().Be(Path.Combine(root, "presets.json"));
        FFAppPaths.ComparisonsDirectory().Should().Be(Path.Combine(root, "comparisons"));
    }
}
