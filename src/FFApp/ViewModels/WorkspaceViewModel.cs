using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Helpers;
using FFApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FFApp.ViewModels;

public partial class WorkspaceViewModel : ObservableObject,
    IRecipient<SourceLoadedMessage>,
    IRecipient<OutputSettingsChangedMessage>
{
    public SourcePanelViewModel SourcePanel { get; }
    public OutputSettingsViewModel OutputSettings { get; }
    public PresetViewModel Preset { get; }
    public CommandPreviewViewModel CommandPreview { get; }
    public JobsPanelViewModel JobsPanel { get; }

    // Convenience accessors for drag-drop in MainWindowViewModel
    public QueuePanelViewModel QueuePanel => JobsPanel.QueuePanel;

    public WorkspaceViewModel(IServiceProvider services)
    {
        var dialogService = services.GetRequiredService<IDialogService>();
        var messenger = services.GetRequiredService<IMessenger>();

        SourcePanel = new SourcePanelViewModel(
            services.GetRequiredService<IMediaProbeService>(), dialogService, messenger);

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

        messenger.Register<SourceLoadedMessage>(this);
        messenger.Register<OutputSettingsChangedMessage>(this);
    }

    public async Task LoadAsync()
    {
        await Preset.LoadPresetsAsync();
    }

    public void Receive(SourceLoadedMessage message)
    {
        var mediaInfo = message.Value;
        OutputSettings.UpdateSourceInfo(mediaInfo);
        CommandPreview.UpdateSourceInfo(mediaInfo);
        JobsPanel.UpdateSourceInfo(mediaInfo);
    }

    public void Receive(OutputSettingsChangedMessage message)
    {
        var settings = message.Value;
        CommandPreview.UpdateOutputSettings(settings);
        Preset.UpdateOutputSettings(settings);
        JobsPanel.UpdateOutputSettings(settings);
    }
}
