using FFApp.Services;
using FluentAssertions;

namespace FFApp.Tests;

public sealed class CompanionAppServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"FFApp companion {Guid.NewGuid():N}");
    private static string Executable => OperatingSystem.IsWindows() ? "FFRux.exe" : "FFRux";

    [Fact]
    public void FindCompanionPath_UsesTheCompanionInsideTheRelocatedInstallation()
    {
        var baseDirectory = Path.Combine(_root, "relocated FFApp");
        string expected;
        string executable;
        if (OperatingSystem.IsMacOS())
        {
            baseDirectory = Path.Combine(baseDirectory, "FFApp.app", "Contents", "MacOS");
            expected = Path.GetFullPath(Path.Combine(baseDirectory, "..", "Helpers", "FFRux.app"));
            executable = Path.Combine(expected, "Contents", "MacOS", "FFRux");
        }
        else
        {
            expected = Path.Combine(baseDirectory, "companion", "FFRux", Executable);
            executable = expected;
        }
        CreateFile(executable);
        CompanionAppService.FindCompanionPath(baseDirectory).Should().Be(expected);
    }

    [Fact]
    public void FindCompanionPath_SourceBuildUsesTheMatchingConfigurationAndFramework()
    {
        var source = Path.Combine(_root, "src");
        var baseDirectory = Path.Combine(source, "FFApp", "bin", "Debug", "net10.0");
        CreateFile(Path.Combine(source, "FFApp", "FFApp.csproj"));
        var expected = Path.Combine(source, "FFRux", "bin", "Debug", "net10.0", Executable);
        CreateFile(expected);
        CreateFile(Path.Combine(source, "FFRux", "bin", "Release", "net10.0", Executable));
        CompanionAppService.FindCompanionPath(baseDirectory).Should().Be(expected);
    }

    [Fact]
    public void FindCompanionPath_MissingCompanionExplainsHowToRepairTheInstallation()
    {
        var act = () => CompanionAppService.FindCompanionPath(Path.Combine(_root, "incomplete"));
        act.Should().Throw<FileNotFoundException>().WithMessage("*Reinstall*dotnet build*");
    }

    private static void CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        File.WriteAllText(path, "test executable");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
