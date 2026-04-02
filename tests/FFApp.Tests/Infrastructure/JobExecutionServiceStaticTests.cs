using FFApp.Core.Models;
using FFApp.Helpers;
using FFApp.Infrastructure;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public class JobExecutionServiceStaticTests
{
    // --- EffectiveOutputDuration ---

    private static JobDefinition DefaultJob() => new TestJobDefinitionBuilder().Build();

    private static JobDefinition TrimmedJob(double? start = null, double? end = null) =>
        FormatHelpers.BuildJobDefinition("/test/source.mp4", "/test/output.mp4", new OutputSettings(),
            trimStartSeconds: start, trimEndSeconds: end);

    [Fact]
    public void EffectiveOutputDuration_NoTrim_ReturnsFullDuration()
    {
        var media = new TestMediaInfoBuilder().WithDuration(120.0).Build();
        DefaultJobExecutionService.EffectiveOutputDuration(DefaultJob(), media).Should().Be(120.0);
    }

    [Fact]
    public void EffectiveOutputDuration_WithTrimStartAndEnd_ReturnsTrimmedDuration()
    {
        var media = new TestMediaInfoBuilder().WithDuration(120.0).Build();
        var job = TrimmedJob(start: 30.0, end: 50.0);
        DefaultJobExecutionService.EffectiveOutputDuration(job, media).Should().Be(20.0);
    }

    [Fact]
    public void EffectiveOutputDuration_WithTrimStartOnly_ReturnsFromStartToEnd()
    {
        var media = new TestMediaInfoBuilder().WithDuration(120.0).Build();
        var job = TrimmedJob(start: 100.0);
        DefaultJobExecutionService.EffectiveOutputDuration(job, media).Should().Be(20.0);
    }

    [Fact]
    public void EffectiveOutputDuration_WithTrimEndOnly_ReturnsFromZeroToEnd()
    {
        var media = new TestMediaInfoBuilder().WithDuration(120.0).Build();
        var job = TrimmedJob(end: 30.0);
        DefaultJobExecutionService.EffectiveOutputDuration(job, media).Should().Be(30.0);
    }

    [Fact]
    public void EffectiveOutputDuration_NullDuration_ReturnsNull()
    {
        var media = new TestMediaInfoBuilder().WithNoDuration().Build();
        DefaultJobExecutionService.EffectiveOutputDuration(DefaultJob(), media).Should().BeNull();
    }

    // --- EstimateOutputFrameCount ---

    [Fact]
    public void EstimateOutputFrameCount_NoTrim_FromFrameCount_ReturnsDirectly()
    {
        var media = new TestMediaInfoBuilder().WithFrameCount(1440).Build();
        DefaultJobExecutionService.EstimateOutputFrameCount(DefaultJob(), media).Should().Be(1440);
    }

    [Fact]
    public void EstimateOutputFrameCount_NoTrim_FromFrameRateAndDuration_Calculates()
    {
        var media = new TestMediaInfoBuilder()
            .WithFrameCount(null)
            .WithFrameRate(24.0)
            .WithDuration(60.0)
            .Build();

        DefaultJobExecutionService.EstimateOutputFrameCount(DefaultJob(), media).Should().Be(1440);
    }

    [Fact]
    public void EstimateOutputFrameCount_NoVideo_ReturnsNull()
    {
        var media = new TestMediaInfoBuilder().WithNoVideo().Build();
        DefaultJobExecutionService.EstimateOutputFrameCount(DefaultJob(), media).Should().BeNull();
    }

    [Fact]
    public void EstimateOutputFrameCount_NoFrameRateNorCount_ReturnsNull()
    {
        var media = new TestMediaInfoBuilder()
            .WithFrameCount(null)
            .WithFrameRate(null)
            .Build();

        DefaultJobExecutionService.EstimateOutputFrameCount(DefaultJob(), media).Should().BeNull();
    }

    [Fact]
    public void EstimateOutputFrameCount_ZeroFrameCount_FallsBackToCalculation()
    {
        var media = new TestMediaInfoBuilder()
            .WithFrameCount(0)
            .WithFrameRate(24.0)
            .WithDuration(60.0)
            .Build();

        DefaultJobExecutionService.EstimateOutputFrameCount(DefaultJob(), media).Should().Be(1440);
    }

    [Fact]
    public void EstimateOutputFrameCount_WithTrim_UsesFrameRateTimesTrimDuration()
    {
        var media = new TestMediaInfoBuilder()
            .WithFrameCount(1440)
            .WithFrameRate(24.0)
            .WithDuration(60.0)
            .Build();
        var job = TrimmedJob(start: 10.0, end: 30.0);

        // 24 fps * 20 seconds = 480 frames (ignores source FrameCount when trimming)
        DefaultJobExecutionService.EstimateOutputFrameCount(job, media).Should().Be(480);
    }

    // --- ResolveJobState ---

    [Fact]
    public void ResolveJobState_EmptyList_ReturnsFailed()
    {
        DefaultJobExecutionService.ResolveJobState([]).Should().Be(JobState.Failed);
    }

    [Fact]
    public void ResolveJobState_SingleCompleted_ReturnsCompleted()
    {
        var results = new List<ProcessExecutionResult>
        {
            MakeResult(JobState.Completed),
        };
        DefaultJobExecutionService.ResolveJobState(results).Should().Be(JobState.Completed);
    }

    [Fact]
    public void ResolveJobState_MultipleLastFailed_ReturnsFailed()
    {
        var results = new List<ProcessExecutionResult>
        {
            MakeResult(JobState.Completed),
            MakeResult(JobState.Failed),
        };
        DefaultJobExecutionService.ResolveJobState(results).Should().Be(JobState.Failed);
    }

    [Fact]
    public void ResolveJobState_LastCanceled_ReturnsCanceled()
    {
        var results = new List<ProcessExecutionResult>
        {
            MakeResult(JobState.Completed),
            MakeResult(JobState.Canceled),
        };
        DefaultJobExecutionService.ResolveJobState(results).Should().Be(JobState.Canceled);
    }

    // --- CombineProcessLogs ---

    [Fact]
    public void CombineProcessLogs_SinglePass_ReturnsDirectStderr()
    {
        var results = new List<ProcessExecutionResult>
        {
            MakeResult(JobState.Completed, "encoding log here"),
        };
        DefaultJobExecutionService.CombineProcessLogs(results).Should().Be("encoding log here");
    }

    [Fact]
    public void CombineProcessLogs_TwoPass_CombinesWithHeaders()
    {
        var results = new List<ProcessExecutionResult>
        {
            MakeResult(JobState.Completed, "pass 1 log"),
            MakeResult(JobState.Completed, "pass 2 log"),
        };

        var combined = DefaultJobExecutionService.CombineProcessLogs(results);
        combined.Should().Contain("[Pass 1]");
        combined.Should().Contain("[Pass 2]");
        combined.Should().Contain("pass 1 log");
        combined.Should().Contain("pass 2 log");
    }

    [Fact]
    public void CombineProcessLogs_EmptyStderr_SkipsPass()
    {
        var results = new List<ProcessExecutionResult>
        {
            MakeResult(JobState.Completed, ""),
            MakeResult(JobState.Completed, "pass 2 log"),
        };

        var combined = DefaultJobExecutionService.CombineProcessLogs(results);
        combined.Should().NotContain("[Pass 1]");
        combined.Should().Contain("[Pass 2]");
    }

    private static ProcessExecutionResult MakeResult(JobState state, string stderr = "") =>
        new(new GeneratedCommand("ffmpeg", [], "ffmpeg ..."), state, state == JobState.Completed ? 0 : 1, 10.0, stderr);
}
