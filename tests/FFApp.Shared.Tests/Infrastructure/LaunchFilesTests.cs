using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Shared.Tests.Infrastructure;

public sealed class LaunchFilesTests
{
    [Fact]
    public void IsInsideRuntimeDirectory_AcceptsPathOutsideBaseDirectory()
    {
        // Pick a path that is guaranteed to live outside AppContext.BaseDirectory.
        var outside = Path.Combine(Path.GetTempPath(), "some-user-file.mp4");

        LaunchFiles.IsInsideRuntimeDirectory(outside).Should().BeFalse();
    }

    [Fact]
    public void IsInsideRuntimeDirectory_RelativeDllName_RejectedWhenResolvedIntoRuntimeDir()
    {
        // This is the macOS `dotnet run` scenario: NSApplication hands the managed assembly over as a
        // relative path "<App>.dll" that the CWD resolves into the runtime directory. Build the relative
        // path from the current CWD instead of changing it, since CWD is process-wide and tests run in parallel.
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, typeof(LaunchFiles).Assembly.Location);
        Path.IsPathRooted(relative).Should().BeFalse();

        LaunchFiles.IsInsideRuntimeDirectory(relative).Should().BeTrue();
    }

    [Fact]
    public void IsInsideRuntimeDirectory_HandlesInvalidPathGracefully()
    {
        LaunchFiles.IsInsideRuntimeDirectory("\0\0invalid").Should().BeFalse();
    }

    [Fact]
    public void FilterExistingPaths_KeepsExistingFilesAndFoldersInOrder()
    {
        var folder = Directory.CreateTempSubdirectory("launch-files-");
        try
        {
            var file = Path.Combine(folder.FullName, "movie.mkv");
            File.WriteAllBytes(file, []);

            LaunchFiles.FilterExistingPaths([file, folder.FullName])
                .Should().Equal(file, folder.FullName);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void FilterExistingPaths_DropsBlankMissingAndRuntimePaths()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mkv");
        var runtimeFile = typeof(LaunchFiles).Assembly.Location;

        LaunchFiles.FilterExistingPaths(["", "   ", missing, runtimeFile])
            .Should().BeEmpty();
    }
}
