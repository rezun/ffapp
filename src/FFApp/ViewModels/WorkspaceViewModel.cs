using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Helpers;
using FFApp.Infrastructure;
using FFApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FFApp.ViewModels;

public partial class WorkspaceViewModel : ObservableObject,
    IRecipient<SourceLoadedMessage>,
    IRecipient<OutputSettingsChangedMessage>
{
    public SourcePanelViewModel SourcePanel { get; }
    public OutputPathViewModel OutputPath { get; }
    public OutputSettingsViewModel OutputSettings { get; }
    public PresetViewModel Preset { get; }
    public CommandPreviewViewModel CommandPreview { get; }
    public JobsPanelViewModel JobsPanel { get; }
    public VideoPreviewViewModel VideoPreview { get; }

    // Convenience accessors for drag-drop in MainWindowViewModel
    public QueuePanelViewModel QueuePanel => JobsPanel.QueuePanel;

    public WorkspaceViewModel(IServiceProvider services)
    {
        var dialogService = services.GetRequiredService<IDialogService>();
        var messenger = services.GetRequiredService<IMessenger>();

        SourcePanel = new SourcePanelViewModel(
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

        var execution = new ExecutionViewModel(
            services.GetRequiredService<IJobExecutionService>(), dialogService);

        var queuePanel = new QueuePanelViewModel(
            services.GetRequiredService<IQueueService>(),
            services.GetRequiredService<IMediaProbeService>(),
            services.GetRequiredService<ICommandBuilder>(),
            services.GetRequiredService<IValidationService>(),
            dialogService);

        JobsPanel = new JobsPanelViewModel(execution, queuePanel);

        VideoPreview = new VideoPreviewViewModel(VlcLocator.TryCreate());
        VideoPreview.PropertyChanged += OnVideoPreviewPropertyChanged;
        OutputSettings.PropertyChanged += OnOutputSettingsPropertyChanged;
        Preset.PropertyChanged += OnPresetPropertyChanged;

        messenger.Register<SourceLoadedMessage>(this);
        messenger.Register<OutputSettingsChangedMessage>(this);
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
            JobsPanel.Execution.UpdateHasValidationErrors(OutputSettings.HasValidationErrors);
        }
    }

    private void OnVideoPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VideoPreviewViewModel.TrimInSeconds)
                           or nameof(VideoPreviewViewModel.TrimOutSeconds))
        {
            var trimStart = VideoPreview.TrimInSeconds > 0 ? VideoPreview.TrimInSeconds : (double?)null;
            var trimEnd = VideoPreview.TrimOutSeconds > 0
                          && VideoPreview.TrimOutSeconds < VideoPreview.DurationMs / 1000.0
                ? VideoPreview.TrimOutSeconds : (double?)null;

            CommandPreview.UpdateTrim(trimStart, trimEnd);
            JobsPanel.Execution.UpdateTrim(trimStart, trimEnd);
        }

        if (e.PropertyName is nameof(VideoPreviewViewModel.CropX)
                           or nameof(VideoPreviewViewModel.CropY)
                           or nameof(VideoPreviewViewModel.CropW)
                           or nameof(VideoPreviewViewModel.CropH))
        {
            var crop = new CropRect(
                VideoPreview.CropX, VideoPreview.CropY,
                VideoPreview.CropW, VideoPreview.CropH);

            CommandPreview.UpdateCrop(crop);
            JobsPanel.Execution.UpdateCrop(crop);
        }
    }

    private void OnOutputPathPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OutputPathViewModel.OutputDirectory)
                           or nameof(OutputPathViewModel.OutputFilename)
                           or nameof(OutputPathViewModel.IsAutoName))
        {
            PushOutputPathOverrides();
        }

        if (e.PropertyName is nameof(OutputPathViewModel.ResolvedPath))
        {
            OutputSettings.UpdateResolvedOutputPath(OutputPath.ResolvedPath);
        }
    }

    private void PushOutputPathOverrides()
    {
        var dir = OutputPath.GetEffectiveDirectory();
        var name = OutputPath.GetEffectiveFilename();
        CommandPreview.UpdateOutputPath(dir, name);
        JobsPanel.Execution.UpdateOutputPath(dir, name);
        JobsPanel.QueuePanel.UpdateOutputDirectory(dir);
    }

    public async Task LoadAsync()
    {
        await Preset.LoadPresetsAsync();
    }

    public void Receive(SourceLoadedMessage message)
    {
        var mediaInfo = message.Value;
        OutputPath.UpdateFromSource(mediaInfo);
        OutputSettings.UpdateSourceInfo(mediaInfo);
        CommandPreview.UpdateSourceInfo(mediaInfo);
        JobsPanel.UpdateSourceInfo(mediaInfo);
        VideoPreview.LoadMedia(mediaInfo);
    }

    public void Receive(OutputSettingsChangedMessage message)
    {
        var settings = message.Value;
        OutputPath.UpdateContainer(settings.Container);
        CommandPreview.UpdateOutputSettings(settings);
        Preset.UpdateOutputSettings(settings);
        JobsPanel.UpdateOutputSettings(settings);
    }
}
