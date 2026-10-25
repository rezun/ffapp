using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;
using FFApp.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace FFApp.ViewModels;

public partial class MainWindowViewModel : ObservableObject,
    IAsyncInitializable,
    IRecipient<DependenciesResolvedMessage>,
    IRecipient<NavigateToDependencySetupMessage>
{
    private readonly TranscodingPreferences _transcodingPreferences;
    private readonly IComparisonService? _comparisonService;
    [ObservableProperty] private decimal _comparisonCacheLimitGiB = 5;
    [ObservableProperty] private string _comparisonStorageUsage = "";
    [ObservableProperty] private string _comparisonStorageStatus = "";
    [ObservableProperty] private string _deleteAllClipsLabel = "Delete all test clips";

    [RelayCommand]
    private void RefreshComparisonStorage()
    {
        if (_comparisonService is null) return;
        try
        {
            var usage = _comparisonService.GetCacheUsage();
            ComparisonStorageUsage = $"Test clips: {FormatHelpers.FormatStorageSize(usage.TotalBytes)} · "
                + $"Protected by open comparisons: {FormatHelpers.FormatStorageSize(usage.ProtectedBytes)}";
            DeleteAllClipsLabel = $"Delete all unused test clips ({FormatHelpers.FormatStorageSize(usage.ReclaimableBytes)})";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reading comparison storage failed: {ex}");
            ComparisonStorageStatus = $"Could not read storage usage: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteAllComparisonClips()
    {
        if (_comparisonService is null) return;
        try
        {
            var removed = _comparisonService.ClearClips();
            ComparisonStorageStatus = $"Deleted {FormatHelpers.FormatStorageSize(removed)}. Saved comparisons and clips in open comparisons are retained.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clearing comparison storage failed: {ex}");
            ComparisonStorageStatus = $"Could not delete all test clips: {ex.Message}";
        }
        RefreshComparisonStorage();
    }

    [RelayCommand]
    private async Task ApplyComparisonCacheLimitAsync()
    {
        if (_comparisonService is null) return;
        try
        {
            if (ComparisonCacheLimitGiB is < 1 or > 1024 || decimal.Truncate(ComparisonCacheLimitGiB) != ComparisonCacheLimitGiB)
                throw new ArgumentOutOfRangeException(nameof(ComparisonCacheLimitGiB), "Choose a whole number from 1 to 1024 GiB.");
            var updated = (LoadedSettings ?? new AppSettings()) with { ComparisonCacheLimitGiB = (int)ComparisonCacheLimitGiB };
            await _settingsService.SaveAsync(updated);
            UpdateSettings(s => s with { ComparisonCacheLimitGiB = updated.ComparisonCacheLimitGiB });
            _comparisonService.SetCacheLimit(updated.ComparisonCacheLimitGiB * 1024L * 1024 * 1024);
            ComparisonStorageStatus = "Limit saved. Oldest unused clips are removed first. Open comparisons may temporarily exceed the limit.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Updating comparison cache limit failed: {ex}");
            ComparisonStorageStatus = $"Could not apply cache limit: {ex.Message}";
        }
        RefreshComparisonStorage();
    }

    public bool UseLowPriorityTranscoding
    {
        get => _transcodingPreferences.UseLowPriority;
        set
        {
            if (value == _transcodingPreferences.UseLowPriority)
                return;
            _transcodingPreferences.UseLowPriority = value;
            UpdateSettings(s => s with { UseLowPriorityTranscoding = value });
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void ToggleLowPriorityTranscoding() =>
        UseLowPriorityTranscoding = !UseLowPriorityTranscoding;

    private readonly ISettingsService _settingsService;
    private readonly IDependencyService _dependencyService;
    private readonly ExecutablePathsConfig _config;
    private readonly IMessenger _messenger;
    private readonly Func<DependencySetupViewModel> _createSetup;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFfmpegCapabilityService _capabilityService;
    private readonly IComparisonWindowService? _comparisonWindows;
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
        IFfmpegCapabilityService capabilityService,
        TranscodingPreferences transcodingPreferences,
        IComparisonWindowService? comparisonWindows = null, IComparisonService? comparisonService = null)
    {
        _transcodingPreferences = transcodingPreferences;
        _settingsService = settingsService;
        _dependencyService = dependencyService;
        _config = config;
        _messenger = messenger;
        _createSetup = createSetup;
        _scopeFactory = scopeFactory;
        _capabilityService = capabilityService;
        _comparisonWindows = comparisonWindows;
        _comparisonService = comparisonService;

        _messenger.Register<DependenciesResolvedMessage>(this);
        _messenger.Register<NavigateToDependencySetupMessage>(this);
    }

    public Task<bool> CloseComparisonsAsync() => _comparisonWindows?.CloseAllAsync() ?? Task.FromResult(true);

    public async Task InitializeAsync()
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            LoadedSettings = settings;
            ComparisonCacheLimitGiB = settings.ComparisonCacheLimitGiB;
            try
            {
                _comparisonService?.SetCacheLimit(settings.ComparisonCacheLimitGiB * 1024L * 1024 * 1024);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Comparison cache cleanup failed during startup: {ex}");
                ComparisonStorageStatus = $"Could not clean up test clips: {ex.Message}";
            }
            UseLowPriorityTranscoding = settings.UseLowPriorityTranscoding;
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
