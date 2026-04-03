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
    IAsyncInitializable,
    IRecipient<DependenciesResolvedMessage>,
    IRecipient<NavigateToDependencySetupMessage>
{
    private readonly ISettingsService _settingsService;
    private readonly IDependencyService _dependencyService;
    private readonly ExecutablePathsConfig _config;
    private readonly IMessenger _messenger;
    private readonly Func<DependencySetupViewModel> _createSetup;
    private readonly IServiceScopeFactory _scopeFactory;
    private IServiceScope? _workspaceScope;

    [ObservableProperty]
    private object? _currentView;

    public string Title => "FFApp";

    private readonly TaskCompletionSource<AppSettings> _settingsLoaded = new();

    public AppSettings? LoadedSettings { get; private set; }
    public void UpdateSettings(Func<AppSettings, AppSettings> transform) =>
        LoadedSettings = transform(LoadedSettings ?? new AppSettings());
    public ISettingsService SettingsService => _settingsService;
    public Task<AppSettings> SettingsLoadedTask => _settingsLoaded.Task;

    public MainWindowViewModel(
        ISettingsService settingsService,
        IDependencyService dependencyService,
        ExecutablePathsConfig config,
        IMessenger messenger,
        Func<DependencySetupViewModel> createSetup,
        IServiceScopeFactory scopeFactory)
    {
        _settingsService = settingsService;
        _dependencyService = dependencyService;
        _config = config;
        _messenger = messenger;
        _createSetup = createSetup;
        _scopeFactory = scopeFactory;

        _messenger.Register<DependenciesResolvedMessage>(this);
        _messenger.Register<NavigateToDependencySetupMessage>(this);
    }

    public async Task InitializeAsync()
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            LoadedSettings = settings;
            _settingsLoaded.TrySetResult(settings);
            var status = await _dependencyService.DiscoverAsync(settings);

            if (status.IsReady)
            {
                await SwitchToWorkspaceAsync(status);
            }
            else
            {
                var setup = _createSetup();
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

    // async void: IRecipient<T>.Receive must return void — fire-and-forget is unavoidable here.
    public async void Receive(DependenciesResolvedMessage message)
    {
        try
        {
            await SwitchToWorkspaceAsync(message.Value);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to switch to workspace: {ex}");
        }
    }

    public void Receive(NavigateToDependencySetupMessage message)
    {
        var setup = _createSetup();
        CurrentView = setup;
    }

    private async Task SwitchToWorkspaceAsync(DependencyStatus status)
    {
        _config.Configure(status.ExecutablePaths);

        _workspaceScope?.Dispose();
        _workspaceScope = _scopeFactory.CreateScope();

        var workspace = _workspaceScope.ServiceProvider
            .GetRequiredService<WorkspaceViewModel>();
        await workspace.LoadAsync();
        CurrentView = workspace;
    }

    // async void: Called from Avalonia DragDrop event handler — fire-and-forget is unavoidable here.
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
            Debug.WriteLine($"File drop failed: {ex}");
        }
    }
}
