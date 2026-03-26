using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
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
    public ExecutionViewModel Execution { get; }
    public QueuePanelViewModel QueuePanel { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    public WorkspaceViewModel(IServiceProvider services)
    {
        var dialogService = services.GetRequiredService<IDialogService>();

        SourcePanel = new SourcePanelViewModel(
            services.GetRequiredService<IMediaProbeService>(), dialogService);

        OutputSettings = new OutputSettingsViewModel(
            services.GetRequiredService<IValidationService>(),
            services.GetRequiredService<ISettingsService>());

        Preset = new PresetViewModel(
            services.GetRequiredService<IPresetService>(), dialogService,
            settings => OutputSettings.ApplySettings(settings));

        CommandPreview = new CommandPreviewViewModel(
            services.GetRequiredService<ICommandBuilder>(),
            services.GetRequiredService<IValidationService>());

        Execution = new ExecutionViewModel(
            services.GetRequiredService<IJobExecutionService>(), dialogService);

        QueuePanel = new QueuePanelViewModel(
            services.GetRequiredService<IQueueService>(),
            services.GetRequiredService<IMediaProbeService>(),
            services.GetRequiredService<ICommandBuilder>(),
            services.GetRequiredService<IValidationService>(),
            dialogService);

        WeakReferenceMessenger.Default.Register<SourceLoadedMessage>(this);
        WeakReferenceMessenger.Default.Register<OutputSettingsChangedMessage>(this);
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
        Execution.UpdateSourceInfo(mediaInfo);
        QueuePanel.UpdateCurrentContext(mediaInfo);
    }

    public void Receive(OutputSettingsChangedMessage message)
    {
        var settings = message.Value;
        CommandPreview.UpdateOutputSettings(settings);
        Execution.UpdateOutputSettings(settings);
        Preset.UpdateOutputSettings(settings);
        QueuePanel.UpdateCurrentContext(settings);
    }
}
