using System.Runtime.InteropServices;

using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public class DependencyManagerStaticTests
{
    [Fact]
    public void FindExecutable_FindsFileInProvidedDirectory()
    {
        using var tempDir = new TempDirectory();
        var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "tool" : "tool";
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "tool.exe" : "tool";
        var filePath = Path.Combine(tempDir.Path, fileName);
        File.WriteAllText(filePath, string.Empty);

        var result = DependencyManager.FindExecutable(name, [tempDir.Path]);

        result.Should().Be(filePath);
    }

    [Fact]
    public void FindExecutable_ReturnsNullWhenMissing()
    {
        using var tempDir = new TempDirectory();

        DependencyManager.FindExecutable("definitely-not-here", [tempDir.Path]).Should().BeNull();
    }

    [Fact]
    public void FindExecutable_PrefersEarlierDirectory()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "tool.exe" : "tool";
        var firstPath = Path.Combine(first.Path, fileName);
        var secondPath = Path.Combine(second.Path, fileName);
        File.WriteAllText(firstPath, string.Empty);
        File.WriteAllText(secondPath, string.Empty);

        DependencyManager.FindExecutable("tool", [first.Path, second.Path]).Should().Be(firstPath);
    }

    [Fact]
    public void GetFallbackDirectories_OnMacOS_IncludesHomebrewPaths()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        var dirs = DependencyManager.GetFallbackDirectories();

        dirs.Should().Contain("/opt/homebrew/bin");
        dirs.Should().Contain("/usr/local/bin");
    }

    [Fact]
    public void GetFallbackDirectories_OnLinux_IncludesCommonPaths()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        var dirs = DependencyManager.GetFallbackDirectories();

        dirs.Should().Contain("/usr/local/bin");
    }

    [Fact]
    public void GetFallbackDirectories_OnWindows_IsEmpty()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        DependencyManager.GetFallbackDirectories().Should().BeEmpty();
    }

    [Fact]
    public void EnumerateSearchDirectories_AppendsFallbacksAfterPath()
    {
        var dirs = DependencyManager.EnumerateSearchDirectories().ToList();

        foreach (var fallback in DependencyManager.GetFallbackDirectories())
        {
            dirs.Should().Contain(fallback);
        }
    }

    [Fact]
    public void EnumerateSearchDirectories_DoesNotDuplicateDirectories()
    {
        var dirs = DependencyManager.EnumerateSearchDirectories().ToList();

        dirs.Should().OnlyHaveUniqueItems();
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
