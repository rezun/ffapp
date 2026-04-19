using FluentAssertions;

namespace FFApp.Tests;

public sealed class AppFilterTests
{
    [Fact]
    public void IsInsideRuntimeDirectory_RejectsAppsOwnAssembly()
    {
        var ownAssembly = typeof(App).Assembly.Location;

        App.IsInsideRuntimeDirectory(ownAssembly).Should().BeTrue();
    }

    [Fact]
    public void IsInsideRuntimeDirectory_AcceptsPathOutsideBaseDirectory()
    {
        // Pick a path that is guaranteed to live outside AppContext.BaseDirectory.
        var outside = Path.Combine(Path.GetTempPath(), "some-user-file.mp4");

        App.IsInsideRuntimeDirectory(outside).Should().BeFalse();
    }

    [Fact]
    public void IsInsideRuntimeDirectory_RelativeDllName_RejectedWhenResolvedIntoRuntimeDir()
    {
        // This is the exact macOS `dotnet run` scenario: NSApplication hands
        // the managed assembly over as a relative path "FFApp.dll". When the
        // CWD resolves it into the runtime directory, the filter must catch it.
        var runtimeDir = Path.GetDirectoryName(typeof(App).Assembly.Location)!;
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = runtimeDir;
            App.IsInsideRuntimeDirectory("FFApp.dll").Should().BeTrue();
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void IsInsideRuntimeDirectory_HandlesInvalidPathGracefully()
    {
        App.IsInsideRuntimeDirectory("\0\0invalid").Should().BeFalse();
    }
}
