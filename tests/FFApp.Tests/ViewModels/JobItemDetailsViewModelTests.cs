using FFApp.Core.Models;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public sealed class JobItemDetailsViewModelTests
{
    [Fact]
    public void Selection_ExposesPathsCommandsAndLiveProgress_AndClearsOnDeselect()
    {
        var job = new TestJobDefinitionBuilder().WithSourcePath("/input/source.mkv")
            .WithOutputPath("/output/encoded.webm").Build();
        var media = new TestMediaInfoBuilder().Build();
        var queued = new QueuedJob(job, media,
            [new GeneratedCommand("ffmpeg", [], "first pass"), new GeneratedCommand("ffmpeg", [], "second pass")],
            new ValidationResult());
        var item = new JobItemViewModel("inspector-test", media, queued, "source.mkv");
        var details = new JobItemDetailsViewModel();

        details.HasSelection.Should().BeFalse();
        details.UpdateFromItem(item);
        details.HasSelection.Should().BeTrue();
        details.SourcePath.Should().Be(job.SourcePath);
        details.OutputPath.Should().Be(job.OutputPath);
        details.CommandText.Should().Be($"first pass{Environment.NewLine}{Environment.NewLine}second pass");
        item.State = JobState.Running;
        item.UpdateProgress(new JobProgress(JobState.Running, 1, 1, 5, Percentage: 50));
        details.StateText.Should().Be("Running");
        details.ProgressText.Should().Be(item.ProgressText);

        details.UpdateFromItem(null);
        details.HasSelection.Should().BeFalse();
        details.SourcePath.Should().BeEmpty();
        details.OutputPath.Should().BeEmpty();
        details.StateText.Should().BeEmpty();
        details.ProgressText.Should().BeEmpty();
        details.CommandText.Should().BeEmpty();
        details.LogText.Should().BeEmpty();
    }

    [Fact]
    public void SwitchingJobs_KeepsDiagnosticTab_DetachesPreviousJob_AndOpensFailuresOnce()
    {
        var failed = CreateItem();
        failed.UpdateResult(Result(failed, JobState.Failed));
        var current = CreateItem();
        current.AppendLog("current job");
        var details = new JobItemDetailsViewModel();
        details.UpdateFromItem(failed);
        details.SelectedTabIndex = 1;

        details.UpdateFromItem(current);
        failed.UpdateFailure("old job failed again");
        details.SelectedTabIndex.Should().Be(1);
        details.HasFailure.Should().BeFalse();
        details.LogText.Should().Be($"current job{Environment.NewLine}");

        current.UpdateFailure("current job failed");
        details.SelectedTabIndex.Should().Be(0);
        details.SelectedTabIndex = 1;
        current.AppendLog("more diagnostics");
        details.SelectedTabIndex.Should().Be(1, "an existing failure must not repeatedly switch tabs");
        details.LogText.Should().Be($"current job{Environment.NewLine}more diagnostics{Environment.NewLine}");

        details.UpdateFromItem(failed);
        details.SelectedTabIndex.Should().Be(0, "selecting a failed job opens its failure");
        details.SelectedTabIndex = 1;
        details.UpdateFromItem(null);
        details.SelectedTabIndex.Should().Be(0);
        details.HasFailure.Should().BeFalse();
    }

    [Fact]
    public async Task RenameOutput_KeepsTypedNameWhileJobLogsAndPassesItOn()
    {
        var job = new TestJobDefinitionBuilder().WithOutputPath("/output/encoded.webm").Build();
        var media = new TestMediaInfoBuilder().Build();
        var item = new JobItemViewModel("rename-test", media,
            new QueuedJob(job, media, [], new ValidationResult()), "source.mkv");
        (JobItemViewModel Item, string Stem)? request = null;
        var details = new JobItemDetailsViewModel((target, stem) =>
        {
            request = (target, stem);
            return Task.CompletedTask;
        });

        details.UpdateFromItem(item);
        details.OutputFileStem.Should().Be("encoded");
        details.OutputExtension.Should().Be(".webm");
        details.RenameOutputCommand.CanExecute(null).Should().BeFalse("the name is unchanged");

        details.OutputFileStem = "final";
        item.AppendLog("frame=1");
        details.OutputFileStem.Should().Be("final");
        details.RenameOutputCommand.CanExecute(null).Should().BeTrue();

        await details.RenameOutputCommand.ExecuteAsync(null);

        request.Should().Be((item, "final"));
    }

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
