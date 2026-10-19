using FFApp.Core.Models;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public sealed class JobInspectorViewModelTests
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SwitchingJobs_PreservesDiagnosticTab_AndDetachesPreviousJob(int tab)
    {
        var first = JobFailureViewModelTests.CreateItem();
        var second = JobFailureViewModelTests.CreateItem();
        second.AppendLog("second job");
        var details = new JobItemDetailsViewModel();
        details.UpdateFromItem(first);
        details.SelectedTabIndex = tab;

        details.UpdateFromItem(second);
        first.UpdateFailure("old job failed");
        details.SelectedTabIndex.Should().Be(tab);
        details.HasFailure.Should().BeFalse();
        details.LogText.Should().Contain("second job");

        second.UpdateFailure("current job failed");
        details.SelectedTabIndex.Should().Be(0);
        details.SelectedTabIndex = tab;
        second.AppendLog("more diagnostics");
        details.SelectedTabIndex.Should().Be(tab, "an existing failure must not repeatedly switch tabs");
    }
}
