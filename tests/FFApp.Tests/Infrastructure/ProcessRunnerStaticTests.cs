using FFApp.Core.Models;
using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public class ProcessRunnerStaticTests
{
    [Fact]
    public void BuildArguments_Ffmpeg_AddsProgressArguments()
    {
        var command = new GeneratedCommand(
            "ffmpeg", ["-i", "input.mkv", "output.mkv"], "ffmpeg ...");

        var arguments = ProcessRunner.BuildArguments(command);

        arguments.Should().Equal(
            "-progress", "pipe:1", "-nostats",
            "-i", "input.mkv", "output.mkv");
    }

    [Fact]
    public void BuildArguments_Plain_DoesNotAddFfmpegArguments()
    {
        var command = new GeneratedCommand(
            "mkvpropedit",
            ["output.mkv", "--add-track-statistics-tags"],
            "mkvpropedit ...",
            ProcessProtocol.Plain);

        var arguments = ProcessRunner.BuildArguments(command);

        arguments.Should().Equal("output.mkv", "--add-track-statistics-tags");
    }

    // --- ParseTimestamp ---

    [Fact]
    public void ParseTimestamp_ValidFormat_ReturnsCorrectSeconds()
    {
        ProcessRunner.ParseTimestamp("01:23:45.678").Should().BeApproximately(5025.678, 0.001);
    }

    [Fact]
    public void ParseTimestamp_ZeroTimestamp_ReturnsZero()
    {
        ProcessRunner.ParseTimestamp("00:00:00.000").Should().Be(0.0);
    }

    [Fact]
    public void ParseTimestamp_WhitespaceInput_TrimsAndParses()
    {
        ProcessRunner.ParseTimestamp("  00:01:30.000  ").Should().BeApproximately(90.0, 0.001);
    }

    [Fact]
    public void ParseTimestamp_MissingSecondColon_ReturnsNull()
    {
        ProcessRunner.ParseTimestamp("01:23").Should().BeNull();
    }

    [Fact]
    public void ParseTimestamp_NoColons_ReturnsNull()
    {
        ProcessRunner.ParseTimestamp("12345").Should().BeNull();
    }

    [Fact]
    public void ParseTimestamp_NonNumericParts_ReturnsNull()
    {
        ProcessRunner.ParseTimestamp("ab:cd:ef.gh").Should().BeNull();
    }

    [Fact]
    public void ParseTimestamp_LargeHours_ReturnsCorrectly()
    {
        ProcessRunner.ParseTimestamp("100:00:00.000").Should().BeApproximately(360000.0, 0.001);
    }

    // --- ParseSpeed ---

    [Fact]
    public void ParseSpeed_WithXSuffix_ReturnsValue()
    {
        ProcessRunner.ParseSpeed("2.5x").Should().BeApproximately(2.5, 0.001);
    }

    [Fact]
    public void ParseSpeed_WithoutXSuffix_ReturnsValue()
    {
        ProcessRunner.ParseSpeed("1.0").Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void ParseSpeed_WhitespaceInput_TrimsAndParses()
    {
        ProcessRunner.ParseSpeed("  3.5x  ").Should().BeApproximately(3.5, 0.001);
    }

    [Fact]
    public void ParseSpeed_NonNumeric_ReturnsNull()
    {
        ProcessRunner.ParseSpeed("N/A").Should().BeNull();
    }

    [Fact]
    public void ParseSpeed_ZeroSpeed_ReturnsZero()
    {
        ProcessRunner.ParseSpeed("0x").Should().Be(0.0);
    }

    // --- CalculatePercentage ---

    [Fact]
    public void CalculatePercentage_TimeBased_ReturnsTimeBased()
    {
        ProcessRunner.CalculatePercentage(30.0, 60.0, null, null).Should().BeApproximately(50.0, 0.01);
    }

    [Fact]
    public void CalculatePercentage_NoTimeFallsBackToFrame()
    {
        ProcessRunner.CalculatePercentage(null, 60.0, 500, 1000).Should().BeApproximately(50.0, 0.01);
    }

    [Fact]
    public void CalculatePercentage_NeitherAvailable_ReturnsNull()
    {
        ProcessRunner.CalculatePercentage(null, null, null, null).Should().BeNull();
    }

    [Fact]
    public void CalculatePercentage_CapsAt100()
    {
        ProcessRunner.CalculatePercentage(120.0, 60.0, null, null).Should().Be(100.0);
    }

    [Fact]
    public void CalculatePercentage_ZeroDuration_ReturnsNull()
    {
        ProcessRunner.CalculatePercentage(30.0, 0.0, null, null).Should().BeNull();
    }

    [Fact]
    public void CalculatePercentage_ZeroFrameCount_ReturnsNull()
    {
        ProcessRunner.CalculatePercentage(null, null, 500, 0).Should().BeNull();
    }

    [Fact]
    public void CalculatePercentage_ZeroProcessedSeconds_FallsToFrame()
    {
        ProcessRunner.CalculatePercentage(0.0, 60.0, 500, 1000).Should().BeApproximately(50.0, 0.01);
    }

    // --- ParseProgressUpdate ---

    [Fact]
    public void ParseProgressUpdate_CompletePacket_ReturnsAllFields()
    {
        var packet = new Dictionary<string, string>
        {
            ["out_time"] = "00:01:00.000",
            ["speed"] = "2.5x",
            ["frame"] = "1440",
        };

        var result = ProcessRunner.ParseProgressUpdate(packet, 30.0, 120.0, null);

        result.ElapsedSeconds.Should().Be(30.0);
        result.ProcessedSeconds.Should().BeApproximately(60.0, 0.001);
        result.Speed.Should().BeApproximately(2.5, 0.001);
        result.Percentage.Should().BeApproximately(50.0, 0.01);
    }

    [Fact]
    public void ParseProgressUpdate_OnlyOutTime_ReturnsProcessedSeconds()
    {
        var packet = new Dictionary<string, string>
        {
            ["out_time"] = "00:00:30.000",
        };

        var result = ProcessRunner.ParseProgressUpdate(packet, 10.0, 60.0, null);

        result.ProcessedSeconds.Should().BeApproximately(30.0, 0.001);
        result.Percentage.Should().BeApproximately(50.0, 0.01);
    }

    [Fact]
    public void ParseProgressUpdate_OutTimeUsFallback()
    {
        var packet = new Dictionary<string, string>
        {
            ["out_time_us"] = "30000000",
        };

        var result = ProcessRunner.ParseProgressUpdate(packet, 10.0, 60.0, null);
        result.ProcessedSeconds.Should().BeApproximately(30.0, 0.001);
    }

    [Fact]
    public void ParseProgressUpdate_OutTimeMsFallback()
    {
        var packet = new Dictionary<string, string>
        {
            ["out_time_ms"] = "30000",
        };

        var result = ProcessRunner.ParseProgressUpdate(packet, 10.0, 60.0, null);
        result.ProcessedSeconds.Should().BeApproximately(30.0, 0.001);
    }

    [Fact]
    public void ParseProgressUpdate_EmptyPacket_ReturnsMinimalProgress()
    {
        var packet = new Dictionary<string, string>();
        var result = ProcessRunner.ParseProgressUpdate(packet, 5.0, 60.0, null);

        result.ElapsedSeconds.Should().Be(5.0);
        result.ProcessedSeconds.Should().BeNull();
        result.Speed.Should().BeNull();
        result.Percentage.Should().BeNull();
    }

    [Fact]
    public void ParseProgressUpdate_FrameBasedFallback()
    {
        var packet = new Dictionary<string, string>
        {
            ["frame"] = "500",
        };

        var result = ProcessRunner.ParseProgressUpdate(packet, 10.0, null, 1000);
        result.Percentage.Should().BeApproximately(50.0, 0.01);
    }
}
