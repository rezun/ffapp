using FFApp.Core.Models;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public sealed class JobItemViewModelTests
{
    [Fact]
    public void ResetToPending_ClearsRunStateInItemAndSelectedDetails()
    {
        var item = JobItemDetailsViewModelTests.CreateItem();
        item.AppendLog("some log");
        item.LogFilePath = "/logs/job.log";
        item.UpdateProgress(new JobProgress(JobState.Running, 1, 2, 5.0, Percentage: 50));
        item.UpdateResult(JobItemDetailsViewModelTests.Result(item, JobState.Failed));
        var details = new JobItemDetailsViewModel();
        details.UpdateFromItem(item);

        item.ResetToPending();

        item.State.Should().Be(JobState.Pending);
        item.Percentage.Should().Be(0);
        item.ProgressText.Should().BeEmpty();
        item.LogText.Should().BeEmpty();
        item.LogFilePath.Should().BeNull();
        item.Result.Should().BeNull();
        item.Failure.Should().BeNull();
        details.Failure.Should().BeNull();
        details.LogText.Should().BeEmpty();
    }

    [Theory]
    [InlineData(JobState.Completed, 100, false)]
    [InlineData(JobState.Failed, 30, true)]
    public void UpdateResult_CompletesProgressOnlyOnSuccessAndAnalyzesOnlyFailures(
        JobState state, double expectedPercentage, bool expectedFailure)
    {
        var item = JobItemDetailsViewModelTests.CreateItem();
        item.UpdateProgress(new JobProgress(JobState.Running, 1, 1, 5.0, Percentage: 30));
        item.UpdateResult(JobItemDetailsViewModelTests.Result(item, JobState.Failed));
        var result = JobItemDetailsViewModelTests.Result(item, state);

        item.UpdateResult(result);

        item.State.Should().Be(state);
        item.Result.Should().Be(result);
        item.Percentage.Should().Be(expectedPercentage);
        item.HasFailure.Should().Be(expectedFailure);
    }
}
