using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Shared.Tests.Infrastructure;

public sealed class LaunchFilesTests
{
    [Fact]
    public void IsInsideRuntimeDirectory_RejectsAppsOwnAssembly()
    {
        var ownAssembly = typeof(LaunchFiles).Assembly.Location;

        LaunchFiles.IsInsideRuntimeDirectory(ownAssembly).Should().BeTrue();
    }

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
        // This is the exact macOS `dotnet run` scenario: NSApplication hands
        // the managed assembly over as a relative path "<App>.dll". When the
        // CWD resolves it into the runtime directory, the filter must catch it.
        var runtimeDir = Path.GetDirectoryName(typeof(LaunchFiles).Assembly.Location)!;
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = runtimeDir;
            LaunchFiles.IsInsideRuntimeDirectory("FFApp.Desktop.dll").Should().BeTrue();
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
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
