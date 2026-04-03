using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Helpers;
using FFApp.Services;

namespace FFApp.ViewModels;

public partial class SourceListPanelViewModel : ObservableObject
{
    private static readonly string[] MediaFileExtensions =
    [
        ".aac", ".avi", ".flac", ".m4a", ".mkv", ".mov",
        ".mp3", ".mp4", ".mpeg", ".mpg", ".ogg", ".opus",
        ".wav", ".webm",
    ];

    private readonly IMediaProbeService _mediaProbeService;
    private readonly IDialogService _dialogService;
    private readonly IMessenger _messenger;

    public ObservableCollection<SourceFileViewModel> Sources { get; } = [];

    [ObservableProperty]
    private SourceFileViewModel? _selectedSource;

    [ObservableProperty]
    private string _statusText = "No source files loaded.";

    /// <summary>All currently selected sources (driven by the view's SelectionChanged).</summary>
    public IReadOnlyList<SourceFileViewModel> SelectedSources { get; private set; } = [];

    public string MediaSummaryText =>
        SelectedSource?.MediaInfo is { } info ? FormatHelpers.BuildMediaSummaryText(info) : "";

    public SourceListPanelViewModel(
        IMediaProbeService mediaProbeService,
        IDialogService dialogService,
        IMessenger messenger)
    {
        _mediaProbeService = mediaProbeService;
        _dialogService = dialogService;
        _messenger = messenger;
    }

    /// <summary>
    /// Called from the view's SelectionChanged to sync multi-selection state.
    /// </summary>
    public void UpdateSelection(IReadOnlyList<SourceFileViewModel> selectedItems)
    {
        SelectedSources = selectedItems;
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedSourceChanged(SourceFileViewModel? value)
    {
        OnPropertyChanged(nameof(MediaSummaryText));
        RemoveSelectedCommand.NotifyCanExecuteChanged();

        if (value?.MediaInfo is { } mediaInfo)
        {
            _messenger.Send(new SourceLoadedMessage(mediaInfo));
        }
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var paths = await _dialogService.OpenFilesAsync("Select media files");
        if (paths is null) return;

        foreach (var path in paths)
            await AddSinglePathAsync(path);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var folder = await _dialogService.OpenFolderAsync("Select media folder");
        if (folder is null) return;

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => MediaFileExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
            await AddSinglePathAsync(path);
    }

    private bool CanRemoveSelected => SelectedSources.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private void RemoveSelected()
    {
        var toRemove = SelectedSources.ToList();
        if (toRemove.Count == 0) return;

        foreach (var source in toRemove)
            Sources.Remove(source);

        SelectedSources = [];

        // Pick a new focused item if the current one was removed
        if (SelectedSource is not null && !Sources.Contains(SelectedSource))
            SelectedSource = Sources.Count > 0 ? Sources[^1] : null;

        UpdateStatus();
    }

    private bool CanClearAll => Sources.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearAll))]
    private void ClearAll()
    {
        Sources.Clear();
        SelectedSource = null;
        UpdateStatus();
    }

    /// <summary>
    /// Handles drag-drop of files/folders onto the source list.
    /// </summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(f => MediaFileExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

                foreach (var file in files)
                    await AddSinglePathAsync(file);
            }
            else if (File.Exists(path))
            {
                await AddSinglePathAsync(path);
            }
        }
    }

    private async Task AddSinglePathAsync(string path)
    {
        if (!File.Exists(path)) return;

        // Don't add duplicates
        if (Sources.Any(s => string.Equals(s.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
            return;

        var vm = new SourceFileViewModel(path);
        vm.IsProbing = true;
        Sources.Add(vm);
        UpdateStatus();

        try
        {
            var mediaInfo = await _mediaProbeService.ProbeAsync(path);
            vm.InitializeFromMediaInfo(mediaInfo);
            vm.IsProbing = false;

            // Auto-select first added file
            if (Sources.Count == 1 || SelectedSource is null)
                SelectedSource = vm;
        }
        catch (Exception ex)
        {
            vm.SetError(ex.Message);
            vm.IsProbing = false;
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (Sources.Count == 0)
            StatusText = "No source files loaded.";
        else
        {
            var ready = Sources.Count(s => s.MediaInfo is not null);
            var errors = Sources.Count(s => s.HasError);
            var probing = Sources.Count(s => s.IsProbing);

            var parts = new List<string> { $"{Sources.Count} file(s)" };
            if (probing > 0) parts.Add($"{probing} probing");
            if (errors > 0) parts.Add($"{errors} failed");
            StatusText = string.Join(", ", parts);
        }

        ClearAllCommand.NotifyCanExecuteChanged();
    }
}
