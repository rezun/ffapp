using System.Runtime.InteropServices;

using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Shared.Tests.Infrastructure;

public sealed class DependencyManagerStaticTests
{
    [Fact]
    public void FindExecutable_ReturnsNullWhenMissing()
    {
        using var tempDir = new TempDirectory();

        DependencyManager.FindExecutable("definitely-not-here", [tempDir.Path]).Should().BeNull();
    }

    [Fact]
    public void FindExecutable_ReturnsMatchFromEarliestDirectory()
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
    public void EnumerateSearchDirectories_ListsPathEntriesThenFallbacks_EachOnce()
    {
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        DependencyManager.EnumerateSearchDirectories().Should().Equal(
            pathEntries.Concat(DependencyManager.GetFallbackDirectories()).Distinct(StringComparer.Ordinal));
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
