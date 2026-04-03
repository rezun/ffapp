using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FFApp.ViewModels;

public partial class MainWindowViewModel : ObservableObject,
    IRecipient<DependenciesResolvedMessage>,
    IRecipient<NavigateToDependencySetupMessage>
{
    private readonly IServiceProvider _services;
    private readonly IMessenger _messenger;

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
        _messenger = services.GetRequiredService<IMessenger>();

        _messenger.Register<DependenciesResolvedMessage>(this);
        _messenger.Register<NavigateToDependencySetupMessage>(this);

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
            else
            {
                var setup = new DependencySetupViewModel(
                    _services.GetRequiredService<IDependencyService>(),
                    _services.GetRequiredService<ISettingsService>(),
                    _services.GetRequiredService<IDialogService>(),
                    _messenger);
                setup.SetFromSettings(settings);
                CurrentView = setup;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Initialization failed: {ex}");
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
            _services.GetRequiredService<IDialogService>(),
            _messenger);
        CurrentView = setup;
    }

    private async Task SwitchToWorkspaceAsync(DependencyStatus status)
    {
        var config = _services.GetRequiredService<ExecutablePathsConfig>();
        config.Configure(status.ExecutablePaths);

        var workspace = new WorkspaceViewModel(_services);
        await workspace.LoadAsync();
        CurrentView = workspace;
    }

    public async void HandleFileDrop(IReadOnlyList<string> paths)
    {
        if (CurrentView is not WorkspaceViewModel workspace || paths.Count == 0)
            return;

        try
        {
            await workspace.SourceList.AddPathsAsync(paths);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"File drop failed: {ex.Message}");
        }
    }
}
