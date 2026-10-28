using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure;
using FFRux.Models;
using FFRux.Services;

namespace FFRux.ViewModels;

public sealed partial class MainWindowViewModel(
    IDependencyService dependencies,
    ExecutablePathsConfig pathsConfig,
    IMediaProbeService probe,
    IFilePickerService filePicker) : ObservableObject, IAsyncInitializable
{
    private Task? _initialization;

    public ObservableCollection<SourceFileItem> Files { get; } = [];

    [ObservableProperty] private string _statusText = "Looking for FFmpeg…";
    [ObservableProperty] private bool _hasFiles;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PickFilesCommand))]
    private bool _isReady;

    /// <summary>Discovers FFmpeg once. Files opened before discovery finishes wait for it.</summary>
    public Task InitializeAsync() => _initialization ??= DiscoverToolsAsync();

    public async Task OpenFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        await InitializeAsync();

        foreach (var path in paths)
            Files.Add(await ProbeAsync(path, ct));
        HasFiles = Files.Count > 0;
    }

    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task PickFilesAsync()
    {
        var paths = await filePicker.PickMediaFilesAsync();
        await OpenFilesAsync(paths);
    }

    private async Task DiscoverToolsAsync()
    {
        // FFRux has no settings file yet, so discovery relies on PATH and the usual install locations.
        var status = await dependencies.DiscoverAsync(ExecutablePaths.Empty);
        pathsConfig.Configure(status.ExecutablePaths);
        IsReady = status.IsReady;
        StatusText = status.IsReady
            ? $"Using {pathsConfig.FfmpegPath}"
            : "FFmpeg and FFprobe were not found. Install them or add them to PATH, then restart FFRux.";
    }

    private async Task<SourceFileItem> ProbeAsync(string path, CancellationToken ct)
    {
        if (!IsReady)
            return SourceFileItem.Failed(path, "Cannot read this file without FFprobe.");

        try
        {
            return SourceFileItem.FromProbe(await probe.ProbeAsync(path, ct));
        }
        catch (MediaProbeException ex)
        {
            return SourceFileItem.Failed(path, ex.Message);
        }
    }
}
