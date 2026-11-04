using FFApp.Core.Models;
using FFApp.Helpers;
using FFApp.Infrastructure;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public class JobExecutionServiceStaticTests
{
    private static JobDefinition TrimmedJob(double? start = null, double? end = null) =>
        FormatHelpers.BuildJobDefinition("/test/source.mp4", "/test/output.mp4", new OutputSettings(),
            trimStartSeconds: start, trimEndSeconds: end);

    // --- EffectiveOutputDuration ---

    [Theory]
    [InlineData(120.0, null, null, 120.0)]
    [InlineData(120.0, 100.0, null, 20.0)]
    [InlineData(120.0, null, 30.0, 30.0)]
    [InlineData(120.0, 50.0, 30.0, null)]
    [InlineData(null, null, null, null)]
    public void EffectiveOutputDuration_AppliesTrimToSourceDuration(
        double? sourceDuration, double? trimStart, double? trimEnd, double? expected)
    {
        var media = new TestMediaInfoBuilder().WithDuration(sourceDuration).Build();

        DefaultJobExecutionService.EffectiveOutputDuration(TrimmedJob(trimStart, trimEnd), media).Should().Be(expected);
    }

    // --- EstimateOutputFrameCount ---

    [Theory]
    [InlineData(true, 1000, 24.0, null, null, 1000)]
    [InlineData(true, 0, 24.0, null, null, 1440)]
    [InlineData(true, null, null, null, null, null)]
    [InlineData(true, 1000, 24.0, 10.0, 30.0, 480)]
    [InlineData(false, null, null, null, null, null)]
    public void EstimateOutputFrameCount_UsesSourceCountUntrimmed_ElseFrameRateTimesDuration(
        bool hasVideo, int? frameCount, double? frameRate, double? trimStart, double? trimEnd, int? expected)
    {
        var builder = new TestMediaInfoBuilder().WithDuration(60.0);
        var media = (hasVideo ? builder.WithFrameCount(frameCount).WithFrameRate(frameRate) : builder.WithNoVideo()).Build();

        DefaultJobExecutionService.EstimateOutputFrameCount(TrimmedJob(trimStart, trimEnd), media).Should().Be(expected);
    }

    // --- ResolveJobState ---

    [Theory]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Completed, JobState.Completed)]
    [InlineData(JobState.Canceled, JobState.Completed, JobState.Canceled)]
    public void ResolveJobState_ReturnsLastProcessState_OrFailedWhenNothingRan(JobState expected, params JobState[] states)
    {
        DefaultJobExecutionService.ResolveJobState([.. states.Select(state => MakeResult(state))]).Should().Be(expected);
    }

    // --- CombineProcessLogs ---

    [Fact]
    public void CombineProcessLogs_SinglePass_ReturnsDirectStderr()
    {
        DefaultJobExecutionService.CombineProcessLogs([MakeResult(JobState.Completed, "encoding log here")])
            .Should().Be("encoding log here");
    }

    [Fact]
    public void CombineProcessLogs_MultiplePasses_LabelsEachPass_AndSkipsEmptyOutput()
    {
        var combined = DefaultJobExecutionService.CombineProcessLogs(
        [
            MakeResult(JobState.Completed, ""),
            MakeResult(JobState.Completed, "pass 2 log"),
            MakeResult(JobState.Completed, "pass 3 log"),
        ]);

        var nl = Environment.NewLine;
        combined.Should().Be($"[Pass 2]{nl}pass 2 log{nl}{nl}[Pass 3]{nl}pass 3 log");
    }

    private static ProcessExecutionResult MakeResult(JobState state, string stderr = "") =>
        new(new GeneratedCommand("ffmpeg", [], "ffmpeg ..."), state, state == JobState.Completed ? 0 : 1, 10.0, stderr);
}
