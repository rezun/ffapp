using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFApp.ViewModels;

public partial class WorkspaceViewModel : ObservableObject,
    IRecipient<SourceLoadedMessage>,
    IRecipient<OutputSettingsChangedMessage>
{
    public SourceListPanelViewModel SourceList { get; }
    public OutputPathViewModel OutputPath { get; }
    public OutputSettingsViewModel OutputSettings { get; }
    public PresetViewModel Preset { get; }
    public CommandPreviewViewModel CommandPreview { get; }
    public JobListPanelViewModel JobList { get; }
    public VideoPreviewViewModel VideoPreview { get; }

    private SourceMediaInfo? _currentMediaInfo;
    private readonly ILogger _logger;
    public OutputSettings? CurrentSettings { get; private set; }
    private SourceFileViewModel? _previousSource;
    private readonly IComparisonWindowService? _comparisonWindows;
    private JobContext? _comparisonContext;
    public bool CanCompare => SourceList.SelectedSource?.MediaInfo is not null
        && _comparisonContext is { MediaInfo.PrimaryVideoStream: not null,
            MediaInfo.DurationSeconds: > 0, Settings.VideoMode: VideoMode.Reencode } context
        && double.IsFinite(context.MediaInfo.DurationSeconds.Value)
        && string.Equals(SourceList.SelectedSource.SourcePath, context.MediaInfo.SourcePath, StringComparison.Ordinal);
    [ObservableProperty] private string _comparisonStatus = "";

    public WorkspaceViewModel(
        SourceListPanelViewModel sourceList,
        OutputPathViewModel outputPath,
        OutputSettingsViewModel outputSettings,
        PresetViewModel preset,
        CommandPreviewViewModel commandPreview,
        JobListPanelViewModel jobList,
        VideoPreviewViewModel videoPreview,
        IMessenger messenger,
        IComparisonWindowService? comparisonWindows = null,
        ILogger<WorkspaceViewModel>? logger = null)
    {
        _logger = logger ?? NullLogger<WorkspaceViewModel>.Instance;
        SourceList = sourceList;
        OutputPath = outputPath;
        OutputSettings = outputSettings;
        Preset = preset;
        CommandPreview = commandPreview;
        JobList = jobList;
        VideoPreview = videoPreview;
        _comparisonWindows = comparisonWindows;
        SourceList.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SourceListPanelViewModel.SelectedSource)) return;
            OnPropertyChanged(nameof(CanCompare));
            OpenComparisonCommand.NotifyCanExecuteChanged();
        };

        // Wire PresetViewModel's apply callback to OutputSettingsViewModel
        Preset.PresetApplied += settings => OutputSettings.ApplySettings(settings);

        OutputPath.PropertyChanged += OnOutputPathPropertyChanged;
        VideoPreview.PropertyChanged += OnVideoPreviewPropertyChanged;
        OutputSettings.PropertyChanged += OnOutputSettingsPropertyChanged;
        OutputSettings.Audio.PropertyChanged += OnAudioPropertyChanged;
        OutputSettings.StreamHandling.PropertyChanged += OnStreamHandlingPropertyChanged;
        OutputPath.PropertyChanged += OnOutputPathContainerChanged;
        Preset.PropertyChanged += OnPresetPropertyChanged;

        messenger.Register<SourceLoadedMessage>(this);
        messenger.Register<OutputSettingsChangedMessage>(this);
    }

    private void RebuildAndPushJobContext()
    {
        if (_currentMediaInfo is null || CurrentSettings is null)
        {
            CommandPreview.UpdateJobContext(null);
            JobList.UpdateJobContext(null);
            OutputSettings.UpdateEffectiveDuration(null);
            _comparisonContext = null;
            OnPropertyChanged(nameof(CanCompare));
            OpenComparisonCommand.NotifyCanExecuteChanged();
            return;
        }

        var trimStart = VideoPreview.TrimInSeconds > 0
            ? VideoPreview.TrimInSeconds : (double?)null;
        var trimEnd = VideoPreview.TrimOutSeconds > 0
                      && VideoPreview.TrimOutSeconds < VideoPreview.DurationMs / 1000.0
            ? VideoPreview.TrimOutSeconds : (double?)null;

        var crop = new CropRect(
            VideoPreview.CropX, VideoPreview.CropY,
            VideoPreview.CropW, VideoPreview.CropH);

        var context = new JobContext(
            _currentMediaInfo, CurrentSettings,
            trimStart, trimEnd, crop,
            OutputDirectory: OutputPath.GetEffectiveDirectory(),
            OutputPresetName: OutputPath.PresetName,
            SelectedAudioStreamIndices: OutputSettings.Audio.SelectedAudioStreamIndices,
            SelectedSubtitleStreamIndices: OutputSettings.StreamHandling.SelectedSubtitleStreamIndices);

        _comparisonContext = context;
        OnPropertyChanged(nameof(CanCompare));
        OpenComparisonCommand.NotifyCanExecuteChanged();

        CommandPreview.UpdateJobContext(context);
        JobList.UpdateJobContext(context);
        OutputSettings.UpdateEffectiveDuration(context.EffectiveDurationSeconds);
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task OpenComparisonAsync()
    {
        if (_comparisonWindows is null || _comparisonContext is not { } context) return;
        try
        {
            await _comparisonWindows.ShowAsync(context, settings =>
            {
                if (!string.Equals(SourceList.SelectedSource?.SourcePath, context.MediaInfo.SourcePath, StringComparison.Ordinal))
                    return false;
                OutputSettings.ApplySettings(settings);
                return true;
            });
            ComparisonStatus = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening comparison failed");
            ComparisonStatus = $"Could not open comparison: {ex.Message}";
        }
    }

    private void OnAudioPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioViewModel.SelectedAudioStreamIndices))
        {
            if (SourceList.SelectedSource is { } source)
                source.SelectedAudioStreamIndices = OutputSettings.Audio.SelectedAudioStreamIndices;
            RebuildAndPushJobContext();
        }
    }

    private void OnStreamHandlingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StreamHandlingViewModel.SelectedSubtitleStreamIndices))
        {
            if (SourceList.SelectedSource is { } source)
                source.SelectedSubtitleStreamIndices = OutputSettings.StreamHandling.SelectedSubtitleStreamIndices;
            RebuildAndPushJobContext();
        }
    }

    private void OnPresetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PresetViewModel.SelectedPreset))
        {
            var name = Preset.IsUserPresetSelected ? Preset.SelectedPreset?.Name : null;
            OutputPath.UpdatePresetName(name);
        }
    }

    private void OnOutputSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OutputSettingsViewModel.HasValidationErrors))
        {
            JobList.UpdateHasValidationErrors(OutputSettings.HasValidationErrors);
        }
    }

    private void OnVideoPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VideoPreviewViewModel.TrimInSeconds)
                           or nameof(VideoPreviewViewModel.TrimOutSeconds)
                           or nameof(VideoPreviewViewModel.CropX)
                           or nameof(VideoPreviewViewModel.CropY)
                           or nameof(VideoPreviewViewModel.CropW)
                           or nameof(VideoPreviewViewModel.CropH))
        {
            RebuildAndPushJobContext();
        }
    }

    private void OnOutputPathContainerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutputPathViewModel.SelectedContainer))
        {
            // User changed container in the Output card — sync to FormatViewModel
            var container = OutputPath.SelectedContainer;
            if (OutputSettings.Format.SelectedContainer != container)
                OutputSettings.Format.SelectedContainer = container;
        }
    }

    private void OnOutputPathPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OutputPathViewModel.LocationMode)
                           or nameof(OutputPathViewModel.CustomFolder)
                           or nameof(OutputPathViewModel.PresetName))
        {
            RebuildAndPushJobContext();
        }

        if (e.PropertyName is nameof(OutputPathViewModel.ResolvedPath))
        {
            OutputSettings.UpdateResolvedOutputPath(OutputPath.ResolvedPath);
        }
    }

    public async Task LoadAsync(AppSettings appSettings)
    {
        OutputPath.RestoreLocation(appSettings.OutputLocation);
        await Preset.LoadPresetsAsync();

        // Restore last-used output settings into the workspace. This fires an
        // OutputSettingsChangedMessage, which in turn calls Preset.SyncWithSettings —
        // so any value-equality match against a saved preset will auto-select it.
        OutputSettings.ApplySettings(appSettings.LastUsedSettings);

        // If a preset was selected at exit, re-establish the association. When the
        // stored blob matches the preset exactly, SyncWithSettings has already done
        // this via value-equality. When it differs, this call selects the preset
        // without overwriting the restored settings, leaving it in dirty state.
        if (Preset.SelectedPreset is null)
            Preset.RestoreSelection(appSettings.LastUsedPresetName);
    }

    public void Receive(SourceLoadedMessage message)
    {
        // 1. Save current trim/crop to the previous source before switching
        if (_previousSource is not null)
            VideoPreview.SaveTrimCropToSource(_previousSource);

        // 2. Snapshot "apply to all" values before LoadMedia resets everything
        var globalTrimIn = VideoPreview.TrimInSeconds;
        var globalTrimOut = VideoPreview.TrimOutSeconds;
        var globalCropX = VideoPreview.CropX;
        var globalCropY = VideoPreview.CropY;
        var globalCropW = VideoPreview.CropW;
        var globalCropH = VideoPreview.CropH;

        // 3. Load new media (resets trim/crop to source defaults)
        _currentMediaInfo = message.Value;
        OutputPath.UpdateFromSource(_currentMediaInfo);
        OutputSettings.UpdateSourceInfo(_currentMediaInfo,
            SourceList.SelectedSource?.SelectedAudioStreamIndices,
            SourceList.SelectedSource?.SelectedSubtitleStreamIndices);
        VideoPreview.LoadMedia(_currentMediaInfo);

        // 4. Restore per-file or global trim/crop values
        if (_previousSource is not null)
            _previousSource.PropertyChanged -= OnCurrentSourcePropertyChanged;
        _previousSource = SourceList.SelectedSource;
        if (_previousSource is not null)
        {
            _previousSource.PropertyChanged += OnCurrentSourcePropertyChanged;

            // Per-file restore (respects "apply to all" flags internally)
            VideoPreview.RestoreTrimCropFromSource(_previousSource);
        }

        // 5. If "apply to all" is on, override with the saved global values
        if (VideoPreview.ApplyTrimToAll)
            VideoPreview.SetTrimValues(globalTrimIn, globalTrimOut);

        if (VideoPreview.ApplyCropToAll)
        {
            VideoPreview.CropX = globalCropX;
            VideoPreview.CropY = globalCropY;
            VideoPreview.CropW = globalCropW;
            VideoPreview.CropH = globalCropH;
        }

        SyncCropSuggestion();
        RebuildAndPushJobContext();
    }

    // Crop analysis finishes after the source is loaded, so follow the shown source live.
    private void OnCurrentSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SourceFileViewModel.SuggestedCrop)
                           or nameof(SourceFileViewModel.IsAnalyzingCrop))
        {
            SyncCropSuggestion();
        }
    }

    private void SyncCropSuggestion()
    {
        VideoPreview.SuggestedCrop = _previousSource?.SuggestedCrop;
        VideoPreview.IsAnalyzingCrop = _previousSource?.IsAnalyzingCrop ?? false;
    }

    public void Receive(OutputSettingsChangedMessage message)
    {
        CurrentSettings = message.Value;
        OutputPath.UpdateAvailableContainers(OutputSettings.Format.AvailableContainers);
        OutputPath.UpdateContainer(CurrentSettings.Container);
        Preset.UpdateOutputSettings(CurrentSettings);
        RebuildAndPushJobContext();
    }
}
