using System.Collections.Immutable;
using FFApp.Core.Models;
using FFApp.Infrastructure;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public class JobExecutionServiceStaticTests
{
    // --- EstimateTotalFrameCount ---

    [Fact]
    public void EstimateTotalFrameCount_FromFrameCount_ReturnsDirectly()
    {
        var media = new TestMediaInfoBuilder().WithFrameCount(1440).Build();
        DefaultJobExecutionService.EstimateTotalFrameCount(media).Should().Be(1440);
    }

    [Fact]
    public void EstimateTotalFrameCount_FromFrameRateAndDuration_Calculates()
    {
        var media = new TestMediaInfoBuilder()
            .WithFrameCount(null)
            .WithFrameRate(24.0)
            .WithDuration(60.0)
            .Build();

        DefaultJobExecutionService.EstimateTotalFrameCount(media).Should().Be(1440);
    }

    [Fact]
    public void EstimateTotalFrameCount_NoVideo_ReturnsNull()
    {
        var media = new TestMediaInfoBuilder().WithNoVideo().Build();
        DefaultJobExecutionService.EstimateTotalFrameCount(media).Should().BeNull();
    }

    [Fact]
    public void EstimateTotalFrameCount_NoFrameRateNorCount_ReturnsNull()
    {
        var media = new TestMediaInfoBuilder()
            .WithFrameCount(null)
            .WithFrameRate(null)
            .Build();

        DefaultJobExecutionService.EstimateTotalFrameCount(media).Should().BeNull();
    }

    [Fact]
    public void EstimateTotalFrameCount_ZeroFrameCount_FallsBackToCalculation()
    {
        var media = new TestMediaInfoBuilder()
            .WithFrameCount(0)
            .WithFrameRate(24.0)
            .WithDuration(60.0)
            .Build();

        DefaultJobExecutionService.EstimateTotalFrameCount(media).Should().Be(1440);
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
