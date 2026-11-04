using FFApp.Core.Models;
using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Shared.Tests.Infrastructure;

public sealed class ProcessRunnerStaticTests
{
    [Theory]
    [InlineData(ProcessProtocol.Ffmpeg, new[] { "-progress", "pipe:1", "-nostats", "-i", "in.mkv", "out.mkv" })]
    [InlineData(ProcessProtocol.Plain, new[] { "-i", "in.mkv", "out.mkv" })]
    public void BuildArguments_AddsProgressArgumentsOnlyForFfmpeg(ProcessProtocol protocol, string[] expected)
    {
        var command = new GeneratedCommand("tool", ["-i", "in.mkv", "out.mkv"], "tool ...", protocol);

        ProcessRunner.BuildArguments(command).Should().Equal(expected);
    }

    [Theory]
    [InlineData("01:23:45.678", 5025.678)]
    [InlineData("  00:01:30.000  ", 90.0)]
    [InlineData("01:23", null)]
    [InlineData("12345", null)]
    [InlineData("ab:cd:ef.gh", null)]
    public void ParseTimestamp_ReturnsSecondsOrNullWhenMalformed(string value, double? expected)
    {
        ProcessRunner.ParseTimestamp(value).Should().BeApproximately(expected, 0.001);
    }

    [Theory]
    [InlineData("2.5x", 2.5)]
    [InlineData("1.5", 1.5)]
    [InlineData("  3.5x  ", 3.5)]
    [InlineData("N/A", null)]
    public void ParseSpeed_ReturnsMultiplierOrNull(string value, double? expected)
    {
        ProcessRunner.ParseSpeed(value).Should().BeApproximately(expected, 0.001);
    }

    [Theory]
    [InlineData(30.0, 60.0, null, null, 50.0)]  // time-based
    [InlineData(120.0, 60.0, null, null, 100.0)] // capped
    [InlineData(null, 60.0, 500, 1000, 50.0)]    // no time: frame fallback
    [InlineData(0.0, 60.0, 500, 1000, 50.0)]     // zero time: frame fallback
    [InlineData(30.0, 0.0, null, null, null)]    // zero duration
    [InlineData(null, null, 500, 0, null)]       // zero frame count
    [InlineData(null, null, null, null, null)]
    public void CalculatePercentage_PrefersTimeThenFrames(
        double? processedSeconds, double? duration, int? frame, int? frameCount, double? expected)
    {
        ProcessRunner.CalculatePercentage(processedSeconds, duration, frame, frameCount)
            .Should().BeApproximately(expected, 0.01);
    }

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

    [Theory]
    [InlineData("out_time_us", "30000000", 60.0, null, 30.0, 50.0)]
    [InlineData("out_time_ms", "30000", 60.0, null, 30.0, 50.0)]
    [InlineData("frame", "500", null, 1000, null, 50.0)]
    [InlineData(null, null, 60.0, null, null, null)] // empty packet
    public void ParseProgressUpdate_SingleKeyPacket_UsesFallbacks(
        string? key, string? value, double? duration, int? frameCount,
        double? expectedProcessed, double? expectedPercentage)
    {
        var packet = new Dictionary<string, string>();
        if (key is not null && value is not null)
            packet[key] = value;

        var result = ProcessRunner.ParseProgressUpdate(packet, 5.0, duration, frameCount);

        result.ElapsedSeconds.Should().Be(5.0);
        result.ProcessedSeconds.Should().BeApproximately(expectedProcessed, 0.001);
        result.Speed.Should().BeNull();
        result.Percentage.Should().BeApproximately(expectedPercentage, 0.01);
    }
}
