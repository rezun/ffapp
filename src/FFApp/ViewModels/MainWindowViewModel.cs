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
    private readonly IFfmpegCapabilityService _capabilityService;
    private IServiceScope? _workspaceScope;
    private readonly List<string> _pendingFiles = new();
    private readonly Lock _pendingFilesLock = new();

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
        IServiceScopeFactory scopeFactory,
        IFfmpegCapabilityService capabilityService)
    {
        _settingsService = settingsService;
        _dependencyService = dependencyService;
        _config = config;
        _messenger = messenger;
        _createSetup = createSetup;
        _scopeFactory = scopeFactory;
        _capabilityService = capabilityService;

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
        _capabilityService.IsLibplaceboAvailable = status.IsLibplaceboAvailable;

        _workspaceScope?.Dispose();
        _workspaceScope = _scopeFactory.CreateScope();

        var workspace = _workspaceScope.ServiceProvider
            .GetRequiredService<WorkspaceViewModel>();
        await workspace.LoadAsync(LoadedSettings ?? new AppSettings());
        CurrentView = workspace;

        await FlushPendingFilesAsync(workspace);
    }

    // async void: Called from Avalonia DragDrop event handler — fire-and-forget is unavoidable here.
    public async void HandleFileDrop(IReadOnlyList<string> paths)
    {
        try
        {
            await OpenFilesAsync(paths);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"File drop failed: {ex}");
        }
    }

    /// <summary>
    /// Adds external files to the source list. If the workspace is not yet ready (startup before
    /// dependency discovery completes, or while the dependency setup screen is shown), paths are
    /// queued and flushed once the workspace becomes active.
    /// </summary>
    public async Task OpenFilesAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
            return;

        if (CurrentView is WorkspaceViewModel workspace)
        {
            await workspace.SourceList.AddPathsAsync(paths);
            return;
        }

        lock (_pendingFilesLock)
            _pendingFiles.AddRange(paths);
    }

    internal IReadOnlyList<string> SnapshotPendingFiles()
    {
        lock (_pendingFilesLock)
            return _pendingFiles.ToArray();
    }

    private async Task FlushPendingFilesAsync(WorkspaceViewModel workspace)
    {
        List<string> toAdd;
        lock (_pendingFilesLock)
        {
            if (_pendingFiles.Count == 0)
                return;
            toAdd = new List<string>(_pendingFiles);
            _pendingFiles.Clear();
        }

        try
        {
            await workspace.SourceList.AddPathsAsync(toAdd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Flushing pending file opens failed: {ex}");
        }
    }
}
