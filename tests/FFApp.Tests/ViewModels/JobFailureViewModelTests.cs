using FFApp.Core.Models;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public sealed class JobFailureViewModelTests
{
    [Fact]
    public void SelectedRunningItem_FailureImmediatelyOpensDetailsTab()
    {
        var item = CreateItem();
        var details = new JobItemDetailsViewModel();
        details.UpdateFromItem(item);
        details.SelectedTabIndex = 1;

        item.UpdateResult(Result(item, JobState.Failed));

        item.HasFailure.Should().BeTrue();
        details.HasFailure.Should().BeTrue();
        details.Failure.Should().BeSameAs(item.Failure);
        details.Failure?.Message.Should().Contain("44.1 kHz");
        details.SelectedTabIndex.Should().Be(0);
        details.LogText.Should().Contain("Specified sample rate 44100");
    }

    [Fact]
    public void ResetToPending_ClearsFailureInItemAndSelectedDetails()
    {
        var item = CreateItem();
        item.UpdateResult(Result(item, JobState.Failed));
        var details = new JobItemDetailsViewModel();
        details.UpdateFromItem(item);

        item.ResetToPending();

        item.Failure.Should().BeNull();
        item.HasFailure.Should().BeFalse();
        details.Failure.Should().BeNull();
        details.HasFailure.Should().BeFalse();
        details.LogText.Should().BeEmpty();
    }

    [Theory]
    [InlineData(JobState.Completed)]
    [InlineData(JobState.Canceled)]
    public void UpdateResult_NonFailure_DoesNotShowErrorEvenIfLogContainsErrors(JobState state)
    {
        var item = CreateItem();
        item.UpdateResult(Result(item, JobState.Failed));
        item.UpdateResult(Result(item, state));

        item.Failure.Should().BeNull();
        item.HasFailure.Should().BeFalse();
    }

    [Fact]
    public void SwitchingSelection_RemovesOldFailureAndUnsubscribesOldItem()
    {
        var failed = CreateItem();
        failed.UpdateResult(Result(failed, JobState.Failed));
        var pending = CreateItem();
        var details = new JobItemDetailsViewModel();
        details.UpdateFromItem(failed);
        details.SelectedTabIndex.Should().Be(0);

        details.UpdateFromItem(pending);
        failed.UpdateFailure("Different failure");

        details.HasFailure.Should().BeFalse();
        details.SelectedTabIndex.Should().Be(0);
        details.LogText.Should().BeEmpty();
        details.UpdateFromItem(null);
        details.HasSelection.Should().BeFalse();
        details.Failure.Should().BeNull();
    }

    [Fact]
    public void ExceptionWithoutResult_ShowsPersistentFailureInDetails()
    {
        var item = CreateItem();
        var details = new JobItemDetailsViewModel();
        details.UpdateFromItem(item);

        item.UpdateFailure("Could not start FFmpeg");

        item.State.Should().Be(JobState.Failed);
        item.Result.Should().BeNull();
        details.Failure?.Message.Should().Be("Could not start FFmpeg");
        details.HasFailure.Should().BeTrue();
    }

    internal static JobItemViewModel CreateItem()
    {
        var media = new TestMediaInfoBuilder().Build();
        var job = new QueuedJob(new TestJobDefinitionBuilder().Build(), media, [], new ValidationResult());
        return new JobItemViewModel("failure-test", media, job, "source.mkv");
    }

    internal static JobResult Result(JobItemViewModel item, JobState state) => new(
        item.QueuedJob.Job, state, [], [], 1,
        "[libopus @ 0x123] Specified sample rate 44100 is not supported by the libopus encoder");
}
