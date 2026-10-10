using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;

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
    public OutputSettings? CurrentSettings { get; private set; }
    private SourceFileViewModel? _previousSource;

    public WorkspaceViewModel(
        SourceListPanelViewModel sourceList,
        OutputPathViewModel outputPath,
        OutputSettingsViewModel outputSettings,
        PresetViewModel preset,
        CommandPreviewViewModel commandPreview,
        JobListPanelViewModel jobList,
        VideoPreviewViewModel videoPreview,
        IMessenger messenger)
    {
        SourceList = sourceList;
        OutputPath = outputPath;
        OutputSettings = outputSettings;
        Preset = preset;
        CommandPreview = commandPreview;
        JobList = jobList;
        VideoPreview = videoPreview;

        // Wire PresetViewModel's apply callback to OutputSettingsViewModel
        Preset.PresetApplied += settings => OutputSettings.ApplySettings(settings);

        OutputPath.PropertyChanged += OnOutputPathPropertyChanged;
        VideoPreview.PropertyChanged += OnVideoPreviewPropertyChanged;
        OutputSettings.PropertyChanged += OnOutputSettingsPropertyChanged;
        OutputSettings.Audio.PropertyChanged += OnAudioPropertyChanged;
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
            OutputPath.GetEffectiveDirectory(),
            OutputPath.GetEffectiveFilename(),
            OutputPath.IsAutoName, OutputSettings.Audio.SelectedAudioStreamIndices);

        CommandPreview.UpdateJobContext(context);
        JobList.UpdateJobContext(context);
        OutputSettings.UpdateEffectiveDuration(context.EffectiveDurationSeconds);
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
        if (e.PropertyName is nameof(OutputPathViewModel.OutputDirectory)
                           or nameof(OutputPathViewModel.OutputFilename)
                           or nameof(OutputPathViewModel.IsAutoName))
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
        OutputSettings.UpdateSourceInfo(_currentMediaInfo, SourceList.SelectedSource?.SelectedAudioStreamIndices);
        VideoPreview.LoadMedia(_currentMediaInfo);

        // 4. Restore per-file or global trim/crop values
        _previousSource = SourceList.SelectedSource;
        if (_previousSource is not null)
        {
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

        RebuildAndPushJobContext();
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
