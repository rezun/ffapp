using FFApp.Core.Models;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class JobItemViewModelTests
{
    private static readonly SourceMediaInfo DefaultMedia = new TestMediaInfoBuilder().Build();

    private static JobItemViewModel CreateSut(
        string id = "item-1", string label = "source.mp4")
    {
        var job = new TestJobDefinitionBuilder().Build();
        var queuedJob = new QueuedJob(job, DefaultMedia, [], new ValidationResult());
        return new JobItemViewModel(id, DefaultMedia, queuedJob, label);
    }

    [Fact]
    public void Constructor_SetsInitialState()
    {
        var sut = CreateSut("item-1", "test.mp4");

        sut.Id.Should().Be("item-1");
        sut.DisplayLabel.Should().Be("test.mp4");
        sut.State.Should().Be(JobState.Pending);
        sut.Percentage.Should().Be(0);
        sut.ProgressText.Should().BeEmpty();
        sut.LogText.Should().BeEmpty();
        sut.QueuedJob.Should().NotBeNull();
        sut.Result.Should().BeNull();
    }

    [Fact]
    public void ResetToPending_ClearsEverything()
    {
        var sut = CreateSut();
        sut.AppendLog("some log");
        sut.UpdateProgress(new JobProgress(JobState.Running, 1, 2, 5.0, Percentage: 50));

        sut.ResetToPending();

        sut.State.Should().Be(JobState.Pending);
        sut.Percentage.Should().Be(0);
        sut.ProgressText.Should().BeEmpty();
        sut.LogText.Should().BeEmpty();
        sut.Result.Should().BeNull();
    }

    [Fact]
    public void UpdateProgress_SetsPercentageAndProgressText()
    {
        var sut = CreateSut();
        var progress = new JobProgress(JobState.Running, 1, 2, 10.0, Percentage: 42.5, Speed: 1.5);

        sut.UpdateProgress(progress);

        sut.Percentage.Should().Be(42.5);
        sut.ProgressText.Should().Contain("Pass 1/2");
        sut.ProgressText.Should().NotBeEmpty();
    }

    [Fact]
    public void UpdateResult_Completed_SetsPercentageTo100()
    {
        var sut = CreateSut();
        var result = CreateJobResult(JobState.Completed);

        sut.UpdateResult(result);

        sut.State.Should().Be(JobState.Completed);
        sut.Percentage.Should().Be(100);
        sut.Result.Should().BeSameAs(result);
    }

    [Fact]
    public void UpdateResult_Failed_DoesNotSet100Percent()
    {
        var sut = CreateSut();
        sut.UpdateProgress(new JobProgress(JobState.Running, 1, 1, 5.0, Percentage: 30));

        var result = CreateJobResult(JobState.Failed);
        sut.UpdateResult(result);

        sut.State.Should().Be(JobState.Failed);
        sut.Percentage.Should().Be(30);
    }

    [Fact]
    public void AppendLog_AccumulatesLines()
    {
        var sut = CreateSut();

        sut.AppendLog("line one");
        sut.AppendLog("line two");

        sut.LogText.Should().Contain("line one");
        sut.LogText.Should().Contain("line two");
    }

    private static JobResult CreateJobResult(JobState state) =>
        new(
            Job: new TestJobDefinitionBuilder().Build(),
            State: state,
            Commands: [],
            ProcessResults: [],
            ElapsedSeconds: 1.0,
            LogOutput: "");
}
