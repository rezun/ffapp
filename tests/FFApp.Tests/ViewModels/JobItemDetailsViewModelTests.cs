using FFApp.Core.Models;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public sealed class JobItemDetailsViewModelTests
{

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
