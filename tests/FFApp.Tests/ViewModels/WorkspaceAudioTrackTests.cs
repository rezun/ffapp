using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;
using FFApp.Services;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public sealed class WorkspaceAudioTrackTests
{
    private readonly WorkspaceViewModel _workspace;
    private readonly SourceFileViewModel _first;
    private readonly SourceFileViewModel _second;

    public WorkspaceAudioTrackTests()
    {
        var messenger = new WeakReferenceMessenger();
        var dialog = Substitute.For<IDialogService>();
        var sources = new SourceListPanelViewModel(Substitute.For<IMediaProbeService>(), dialog, messenger);
        var outputPath = new OutputPathViewModel(dialog);
        var paths = new ExecutablePathsConfig();
        paths.Configure(new ExecutablePaths("ffmpeg", "ffprobe"));
        var commandBuilder = new DefaultCommandBuilder(paths);
        var validation = new ValidationService();
        var settings = new OutputSettingsViewModel(validation, messenger, Substitute.For<IFfmpegCapabilityService>(), paths);
        var video = new VideoPreviewViewModel(null);
        var jobs = new JobListPanelViewModel(Substitute.For<IQueueService>(), commandBuilder, validation, dialog, sources, video, outputPath);
        var preset = new PresetViewModel(Substitute.For<IPresetService>(), dialog);
        _workspace = new WorkspaceViewModel(sources, outputPath, settings, preset,
            new CommandPreviewViewModel(commandBuilder), jobs, video, messenger);

        var media = new TestMediaInfoBuilder().WithAudioStreams(
            new AudioStreamInfo(2, "aac", 128_000), new AudioStreamInfo(5, "aac", 128_000)).Build();
        _first = new SourceFileViewModel("/test/first.mp4");
        _first.InitializeFromMediaInfo(media with { SourcePath = _first.SourcePath });
        _second = new SourceFileViewModel("/test/second.mp4");
        _second.InitializeFromMediaInfo(media with { SourcePath = _second.SourcePath });
        sources.Sources.Add(_first);
        sources.Sources.Add(_second);
        sources.SelectedSource = _first;
    }

    private void SelectSecondTrack()
    {
        _workspace.OutputSettings.Audio.AllAudioTracks = false;
        _workspace.OutputSettings.Audio.AudioTracks[0].IsSelected = false;
    }

    [Fact]
    public void SwitchingSources_RestoresPerFileSelection_AndUpdatesCommandPreview()
    {
        SelectSecondTrack();
        _first.SelectedAudioStreamIndices.GetValueOrDefault().Should().Equal(5);
        _workspace.CommandPreview.CommandText.Should().Contain("-map -0:2");
        _workspace.SourceList.SelectedSource = _second;
        _workspace.OutputSettings.Audio.AllAudioTracks.Should().BeTrue();
        _workspace.CommandPreview.CommandText.Should().NotContain("-map -0:2");
        _workspace.SourceList.SelectedSource = _first;
        _workspace.OutputSettings.Audio.SelectedAudioStreamIndices.GetValueOrDefault().Should().Equal(5);
        _workspace.CommandPreview.CommandText.Should().Contain("-map -0:2");
    }

    [Fact]
    public async Task QueueSingleJob_SnapshotsSelection_AndLaterChangesDoNotAlterJob()
    {
        SelectSecondTrack();
        await _workspace.JobList.CreateJobFromSelectionCommand.ExecuteAsync(null);
        _workspace.OutputSettings.Audio.AllAudioTracks = true;
        _workspace.JobList.Jobs.Should().ContainSingle();
        _workspace.JobList.Jobs[0].QueuedJob.Job.SelectedAudioStreamIndices.GetValueOrDefault().Should().Equal(5);
        _workspace.JobList.Jobs[0].QueuedJob.Commands[0].DisplayString.Should().Contain("-map -0:2");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueMultipleJobs_UsesEachSourcesSelection(bool selectedOnly)
    {
        SelectSecondTrack();
        _workspace.SourceList.SelectedSource = _second;
        _workspace.OutputSettings.Audio.AllAudioTracks = false;
        _workspace.OutputSettings.Audio.AudioTracks[1].IsSelected = false;
        if (selectedOnly)
        {
            _workspace.SourceList.UpdateSelection([_first, _second]);
            await _workspace.JobList.CreateJobFromSelectionCommand.ExecuteAsync(null);
        }
        else
        {
            await _workspace.JobList.CreateJobsForAllCommand.ExecuteAsync(null);
        }
        _workspace.JobList.Jobs.Should().HaveCount(2);
        _workspace.JobList.Jobs[0].QueuedJob.Job.SelectedAudioStreamIndices.GetValueOrDefault().Should().Equal(5);
        _workspace.JobList.Jobs[1].QueuedJob.Job.SelectedAudioStreamIndices.GetValueOrDefault().Should().Equal(2);
    }

    [Fact]
    public void TrackSelection_DoesNotMarkPresetDirty_AndPresetChangeKeepsSelection()
    {
        var settings = _workspace.OutputSettings.BuildSettings();
        var preset = new PresetDefinition("Test", settings);
        _workspace.Preset.Presets.Add(preset);
        _workspace.Preset.SelectedPreset = preset;
        SelectSecondTrack();
        _workspace.Preset.IsPresetDirty.Should().BeFalse();
        var other = new PresetDefinition("Other", settings with { Container = OutputContainer.Mkv });
        _workspace.Preset.Presets.Add(other);
        _workspace.Preset.SelectedPreset = other;
        _workspace.OutputSettings.Audio.SelectedAudioStreamIndices.GetValueOrDefault().Should().Equal(5);
    }

    [Fact]
    public void NoSelectedAudio_WithVideoRemoved_IsValidationError()
    {
        SelectSecondTrack();
        _workspace.OutputSettings.Audio.AudioTracks[1].IsSelected = false;
        _workspace.OutputSettings.Format.SelectedVideoMode = VideoMode.Remove;
        _workspace.OutputSettings.HasValidationErrors.Should().BeTrue();
    }
}
