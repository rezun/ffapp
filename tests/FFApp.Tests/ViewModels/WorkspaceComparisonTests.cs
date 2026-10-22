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

public sealed class WorkspaceComparisonTests
{
    private readonly IComparisonWindowService _windows = Substitute.For<IComparisonWindowService>();
    private readonly WorkspaceViewModel _workspace;
    private readonly SourceFileViewModel _source;

    public WorkspaceComparisonTests()
    {
        var messenger = new WeakReferenceMessenger();
        var dialogs = Substitute.For<IDialogService>();
        var sources = new SourceListPanelViewModel(Substitute.For<IMediaProbeService>(), dialogs, messenger);
        var outputPath = new OutputPathViewModel(dialogs);
        var paths = new ExecutablePathsConfig();
        var builder = new DefaultCommandBuilder(paths);
        var validator = new ValidationService();
        var settings = new OutputSettingsViewModel(validator, messenger, Substitute.For<IFfmpegCapabilityService>(), paths);
        var preview = new VideoPreviewViewModel(null);
        var queue = new JobListPanelViewModel(Substitute.For<IQueueService>(), builder, validator, dialogs, sources, preview, outputPath);
        _workspace = new WorkspaceViewModel(sources, outputPath, settings, new PresetViewModel(Substitute.For<IPresetService>(), dialogs),
            new CommandPreviewViewModel(builder), queue, preview, messenger, _windows);
        _source = new SourceFileViewModel("/source.mkv");
        _source.InitializeFromMediaInfo(new TestMediaInfoBuilder().WithSourcePath(_source.SourcePath).Build());
        sources.Sources.Add(_source);
        settings.ApplySettings(new OutputSettings());
    }

    [Fact]
    public void CompareAvailability_RequiresSelectedVideoAndReencoding()
    {
        _workspace.OpenComparisonCommand.CanExecute(null).Should().BeFalse();
        _workspace.SourceList.SelectedSource = _source;
        _workspace.OpenComparisonCommand.CanExecute(null).Should().BeTrue();
        _workspace.OutputSettings.Format.SelectedVideoMode = VideoMode.Copy;
        _workspace.OpenComparisonCommand.CanExecute(null).Should().BeFalse();
        _workspace.OutputSettings.Format.SelectedVideoMode = VideoMode.Reencode;
        _workspace.OpenComparisonCommand.CanExecute(null).Should().BeTrue();
        _workspace.SourceList.SelectedSource = null;
        _workspace.OpenComparisonCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Open_CapturesTrimCropAndSettings_AndAppliesOnlyToSelectedSource()
    {
        _workspace.SourceList.SelectedSource = _source;
        _workspace.VideoPreview.DurationMs = 60000;
        _workspace.VideoPreview.SetTrimValues(10, 40);
        _workspace.VideoPreview.CropX = 20;
        _workspace.VideoPreview.CropW = 1880;
        JobContext? captured = null;
        Func<OutputSettings, bool>? apply = null;
        _windows.ShowAsync(Arg.Do<JobContext>(c => captured = c), Arg.Do<Func<OutputSettings, bool>>(a => apply = a)).Returns(Task.CompletedTask);
        await _workspace.OpenComparisonCommand.ExecuteAsync(null);
        captured.Should().NotBeNull();
        captured?.TrimStartSeconds.Should().Be(10);
        captured?.TrimEndSeconds.Should().Be(40);
        captured?.Crop?.X.Should().Be(20);
        var settings = new OutputSettings(TargetQuality: new H26xQualityOptions(VideoCodec.H264, Crf: 28));
        apply.Should().NotBeNull();
        apply?.Invoke(settings).Should().BeTrue();
        _workspace.CurrentSettings?.TargetQuality.Should().Be(settings.TargetQuality);
        _workspace.SourceList.SelectedSource = null;
        apply?.Invoke(new OutputSettings()).Should().BeFalse();
        _workspace.CurrentSettings?.TargetQuality.Should().Be(settings.TargetQuality);
    }
}
