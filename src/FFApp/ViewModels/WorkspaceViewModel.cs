using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure;
using FFApp.Services;
using Microsoft.Extensions.DependencyInjection;

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
    private OutputSettings? _currentSettings;
    private SourceFileViewModel? _previousSource;

    public WorkspaceViewModel(IServiceProvider services)
    {
        var dialogService = services.GetRequiredService<IDialogService>();
        var messenger = services.GetRequiredService<IMessenger>();

        SourceList = new SourceListPanelViewModel(
            services.GetRequiredService<IMediaProbeService>(), dialogService, messenger);

        OutputPath = new OutputPathViewModel(dialogService);
        OutputPath.PropertyChanged += OnOutputPathPropertyChanged;

        OutputSettings = new OutputSettingsViewModel(
            services.GetRequiredService<IValidationService>(), messenger);

        Preset = new PresetViewModel(
            services.GetRequiredService<IPresetService>(), dialogService,
            settings => OutputSettings.ApplySettings(settings));

        CommandPreview = new CommandPreviewViewModel(
            services.GetRequiredService<ICommandBuilder>(),
            services.GetRequiredService<IValidationService>(),
            dialogService);

        JobList = new JobListPanelViewModel(
            services.GetRequiredService<IQueueService>(),
            services.GetRequiredService<ICommandBuilder>(),
            services.GetRequiredService<IValidationService>(),
            dialogService);

        VideoPreview = new VideoPreviewViewModel(VlcLocator.TryCreate());
        VideoPreview.PropertyChanged += OnVideoPreviewPropertyChanged;
        OutputSettings.PropertyChanged += OnOutputSettingsPropertyChanged;
        OutputPath.PropertyChanged += OnOutputPathContainerChanged;
        Preset.PropertyChanged += OnPresetPropertyChanged;

        // Give JobList references it needs for "create all" operations
        JobList.Initialize(SourceList, VideoPreview, OutputPath);

        messenger.Register<SourceLoadedMessage>(this);
        messenger.Register<OutputSettingsChangedMessage>(this);
    }

    private void RebuildAndPushJobContext()
    {
        if (_currentMediaInfo is null || _currentSettings is null)
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
            _currentMediaInfo, _currentSettings,
            trimStart, trimEnd, crop,
            OutputPath.GetEffectiveDirectory(),
            OutputPath.GetEffectiveFilename(),
            OutputPath.IsAutoName);

        CommandPreview.UpdateJobContext(context);
        JobList.UpdateJobContext(context);
        OutputSettings.UpdateEffectiveDuration(context.EffectiveDurationSeconds);
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

    public async Task LoadAsync()
    {
        await Preset.LoadPresetsAsync();
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
        OutputSettings.UpdateSourceInfo(_currentMediaInfo);
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
        _currentSettings = message.Value;
        OutputPath.UpdateAvailableContainers(OutputSettings.Format.AvailableContainers);
        OutputPath.UpdateContainer(_currentSettings.Container);
        Preset.UpdateOutputSettings(_currentSettings);
        RebuildAndPushJobContext();
    }
}
