using FFApp.Core;
using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public sealed class ComparisonRuntimeServiceTests
{
    [Fact]
    public void MissingSharedLibraries_ReportInstallationHelpWithoutBindingAutoGen()
    {
        var directory = Directory.CreateTempSubdirectory("ffapp-missing-libraries-").FullName;
        try
        {
            var status = ComparisonRuntimeService.CheckDirectory(directory);
            status.IsAvailable.Should().BeFalse();
            status.Message.Should().Contain("FFmpeg 9").And.Contain("shared").And.Contain("architecture");
            // Disposing a never-initialized decoder must not try to load absent native libraries.
            using var factory = new ComparisonDecoderFactory(new ExecutablePathsConfig());
        }
        finally { Directory.Delete(directory, true); }
    }

    [NativeComparisonFact]
    public void InstalledRuntime_CanBeCheckedBeforeOpeningADecoder()
    {
        var runtime = new ComparisonRuntimeService();
        runtime.Check("ffmpeg").IsAvailable.Should().BeTrue();
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "comparison.mp4");
        using var factory = new ComparisonDecoderFactory(new ExecutablePathsConfig());
        using var decoder = factory.Open(source, null);
        decoder.ReadAt(0).Dispose();
        runtime.Check("ffmpeg").IsAvailable.Should().BeTrue();
    }
}
