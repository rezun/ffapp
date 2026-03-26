using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure;
using FFApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FFApp.ViewModels;

public partial class MainWindowViewModel : ObservableObject,
    IRecipient<DependenciesResolvedMessage>,
    IRecipient<NavigateToDependencySetupMessage>
{
    private readonly IServiceProvider _services;

    [ObservableProperty]
    private object? _currentView;

    public string Title => "FFApp";

    private readonly TaskCompletionSource<AppSettings> _settingsLoaded = new();

    public AppSettings? LoadedSettings { get; set; }
    public ISettingsService SettingsService => _services.GetRequiredService<ISettingsService>();
    public Task<AppSettings> SettingsLoadedTask => _settingsLoaded.Task;

    public MainWindowViewModel(IServiceProvider services)
    {
        _services = services;

        CurrentView = new DependencySetupViewModel(
            _services.GetRequiredService<IDependencyService>(),
            _services.GetRequiredService<ISettingsService>(),
            _services.GetRequiredService<IDialogService>());

        WeakReferenceMessenger.Default.Register<DependenciesResolvedMessage>(this);
        WeakReferenceMessenger.Default.Register<NavigateToDependencySetupMessage>(this);

        InitializeAsync();
    }

    private async void InitializeAsync()
    {
        try
        {
            var settingsService = _services.GetRequiredService<ISettingsService>();
            var dependencyService = _services.GetRequiredService<IDependencyService>();

            var settings = await settingsService.LoadAsync();
            LoadedSettings = settings;
            _settingsLoaded.TrySetResult(settings);
            var status = await dependencyService.DiscoverAsync(settings);

            if (status.IsReady)
            {
                await SwitchToWorkspaceAsync(status);
            }
            else if (CurrentView is DependencySetupViewModel setup)
            {
                setup.SetFromSettings(settings);
            }
        }
        catch
        {
            _settingsLoaded.TrySetResult(new AppSettings());
        }
    }

    public async void Receive(DependenciesResolvedMessage message)
    {
        await SwitchToWorkspaceAsync(message.Value);
    }

    public void Receive(NavigateToDependencySetupMessage message)
    {
        var setup = new DependencySetupViewModel(
            _services.GetRequiredService<IDependencyService>(),
            _services.GetRequiredService<ISettingsService>(),
            _services.GetRequiredService<IDialogService>());
        CurrentView = setup;
    }

    private async Task SwitchToWorkspaceAsync(DependencyStatus status)
    {
        var paths = status.ExecutablePaths;
        var commandBuilder = new DefaultCommandBuilder(paths.Ffmpeg ?? "ffmpeg");
        var mediaProbeService = new MediaProbeService(paths.Ffprobe ?? "ffprobe");
        var processRunner = _services.GetRequiredService<IProcessRunner>();

        var workspaceProvider = new ServiceCollection()
            .AddSingleton(_services.GetRequiredService<ISettingsService>())
            .AddSingleton(_services.GetRequiredService<IPresetService>())
            .AddSingleton(_services.GetRequiredService<IValidationService>())
            .AddSingleton(_services.GetRequiredService<IDialogService>())
            .AddSingleton(_services.GetRequiredService<IProcessRunner>())
            .AddSingleton<ICommandBuilder>(commandBuilder)
            .AddSingleton<IMediaProbeService>(mediaProbeService)
            .AddSingleton<IJobExecutionService>(new DefaultJobExecutionService(commandBuilder, processRunner))
            .AddSingleton<IQueueService>(new QueueService(_ =>
                new DefaultJobExecutionService(commandBuilder, processRunner)))
            .BuildServiceProvider();

        var workspace = new WorkspaceViewModel(workspaceProvider);

        await workspace.LoadAsync();
        CurrentView = workspace;
    }

    public void HandleFileDrop(IReadOnlyList<string> paths)
    {
        if (CurrentView is not WorkspaceViewModel workspace || paths.Count == 0)
            return;

        if (workspace.SelectedTabIndex == 1) // Queue tab
        {
            _ = workspace.QueuePanel.AddPathsAsync(paths);
        }
        else // Source tab
        {
            _ = workspace.SourcePanel.LoadSource(paths[0]);
        }
    }
}
