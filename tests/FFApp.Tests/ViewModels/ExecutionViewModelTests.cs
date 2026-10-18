using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public class JobListPanelViewModelTests
{
    private readonly IQueueService _queueService = Substitute.For<IQueueService>();
    private readonly ICommandBuilder _commandBuilder = Substitute.For<ICommandBuilder>();
    private readonly IValidationService _validationService = Substitute.For<IValidationService>();
    private readonly IDialogService _dialogService = Substitute.For<IDialogService>();
    private readonly SourceListPanelViewModel _sourceList;
    private readonly JobListPanelViewModel _sut;

    public JobListPanelViewModelTests()
    {
        _sourceList = new SourceListPanelViewModel(
            Substitute.For<IMediaProbeService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<IMessenger>());
        var videoPreview = new VideoPreviewViewModel(null);
        var outputPath = new OutputPathViewModel(Substitute.For<IDialogService>());

        _sut = new JobListPanelViewModel(
            _queueService, _commandBuilder, _validationService, _dialogService,
            _sourceList, videoPreview, outputPath);

        _validationService.Validate(
                Arg.Any<OutputSettings>(),
                Arg.Any<SourceMediaInfo>(),
                Arg.Any<double?>())
            .Returns(new ValidationResult());
        _commandBuilder.Build(Arg.Any<JobDefinition>(), Arg.Any<SourceMediaInfo>())
            .Returns(callInfo =>
            {
                var outputPath = callInfo.Arg<JobDefinition>().OutputPath;
                return [new GeneratedCommand("ffmpeg", [outputPath], $"ffmpeg {outputPath}")];
            });
    }

    private static JobContext MakeContext() =>
        new(new TestMediaInfoBuilder().Build(), new OutputSettings());

    [Fact]
    public async Task PauseResume_UpdatesQueueAndDisplaysPausedStatus()
    {
        var media = new TestMediaInfoBuilder().WithSourcePath("/test/movie.mp4").Build();
        var queued = new QueuedJob(new TestJobDefinitionBuilder().Build(), media, [], new ValidationResult());
        _sut.Jobs.Add(new JobItemViewModel("a", media, queued, "movie") { State = JobState.Running });
        _sut.IsRunning = true;
        var paused = false;
        _queueService.When(queue => queue.Pause()).Do(_ => paused = true);
        _queueService.When(queue => queue.Resume()).Do(_ => paused = false);
        _queueService.IsPaused.Returns(_ => paused);

        await _sut.PauseResumeCommand.ExecuteAsync(null);
        _sut.IsPaused.Should().BeTrue();
        _sut.StatusText.Should().Be("Paused: movie.mp4");

        await _sut.PauseResumeCommand.ExecuteAsync(null);
        _sut.IsPaused.Should().BeFalse();
        _sut.StatusText.Should().Be("Processing: movie.mp4");
        _queueService.Received(1).Pause();
        _queueService.Received(1).Resume();
    }

    [Fact]
    public async Task PauseResume_OnNativeFailure_ShowsErrorAndPreservesState()
    {
        _sut.IsRunning = true;
        _queueService.When(queue => queue.Pause()).Do(_ => throw new InvalidOperationException("Access denied"));

        await _sut.PauseResumeCommand.ExecuteAsync(null);

        _sut.IsPaused.Should().BeFalse();
        await _dialogService.Received(1).ShowErrorAsync("Pause / Resume Error", "Access denied");
    }

    [Fact]
    public void UpdateHasValidationErrors_SetsProperty()
    {
        _sut.UpdateHasValidationErrors(true);
        _sut.HasValidationErrors.Should().BeTrue();

        _sut.UpdateHasValidationErrors(false);
        _sut.HasValidationErrors.Should().BeFalse();
    }

    [Fact]
    public void UpdateJobContext_Null_IsAccepted()
    {
        _sut.UpdateJobContext(MakeContext());
        _sut.UpdateJobContext(null);
        // Should not throw
    }

    [Fact]
    public void InitialState_HasEmptyIdleQueueAndGuidesUserToAddSources()
    {
        _sut.IsRunning.Should().BeFalse();
        _sut.IsPaused.Should().BeFalse();
        _sut.HasValidationErrors.Should().BeFalse();
        _sut.Jobs.Should().BeEmpty();
        _sut.SummaryText.Should().Be("No jobs.");
        _sut.HasJobs.Should().BeFalse();
        _sut.HasQueueableSelection.Should().BeFalse();
        _sut.AddSelectionLabel.Should().Be("Add selected");
        _sut.AddAllLabel.Should().Be("Add all files");
        _sut.EmptyQueueHint.Should().Be("Add source files and configure their output settings first.");
    }

    [Fact]
    public void QueuePresentation_WithOneSelectedSource_UsesSingularLabels()
    {
        var source = AddReadySource("/test/first.mp4");

        _sourceList.SelectedSource = source;
        _sourceList.UpdateSelection([source]);
        _sut.UpdateJobContext(MakeContext());

        _sut.HasQueueableSelection.Should().BeTrue();
        _sut.CreateJobFromSelectionCommand.CanExecute(null).Should().BeTrue();
        _sut.CreateJobsForAllCommand.CanExecute(null).Should().BeTrue();
        _sut.AddSelectionLabel.Should().Be("Add selected file");
        _sut.AddAllLabel.Should().Be("Add all files");
        _sut.EmptyQueueHint.Should().Be("The selected source will use the current output settings.");
    }

    [Fact]
    public void QueuePresentation_WithMultipleSelectedSources_IncludesCounts()
    {
        var first = AddReadySource("/test/first.mp4");
        var second = AddReadySource("/test/second.mp4");
        AddReadySource("/test/third.mp4");

        _sourceList.SelectedSource = first;
        _sourceList.UpdateSelection([first, second]);

        _sut.AddSelectionLabel.Should().Be("Add 2 selected files");
        _sut.AddAllLabel.Should().Be("Add all 3 files");
        _sut.EmptyQueueHint.Should().Be("The selected sources will use the current output settings.");
    }

    [Fact]
    public async Task CreateJobFromSelection_AutoNameSkipsOutputAlreadyReservedByQueue()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ffapp-{Guid.NewGuid():N}.mp4");
        var source = AddReadySource(sourcePath);
        _sourceList.SelectedSource = source;
        _sourceList.UpdateSelection([source]);
        _sut.UpdateJobContext(new JobContext(
            source.MediaInfo!,
            new OutputSettings { Container = OutputContainer.Mkv },
            OutputDirectory: Path.GetTempPath(),
            IsAutoName: true));

        await _sut.CreateJobFromSelectionCommand.ExecuteAsync(null);
        await _sut.CreateJobFromSelectionCommand.ExecuteAsync(null);

        _sut.Jobs.Should().HaveCount(2);
        _sut.Jobs.Select(job => job.QueuedJob.Job.OutputPath).Should().Equal(
            Path.ChangeExtension(sourcePath, ".mkv"),
            Path.Combine(
                Path.GetDirectoryName(sourcePath)!,
                $"{Path.GetFileNameWithoutExtension(sourcePath)}-1.mkv"));
        _sut.Jobs.Select(job => job.QueuedJob.Commands[0].Arguments[^1])
            .Should().Equal(_sut.Jobs.Select(job => job.QueuedJob.Job.OutputPath));
    }

    private SourceFileViewModel AddReadySource(string path)
    {
        var source = new SourceFileViewModel(path);
        source.InitializeFromMediaInfo(new TestMediaInfoBuilder().WithSourcePath(path).Build());
        _sourceList.Sources.Add(source);
        return source;
    }
}
