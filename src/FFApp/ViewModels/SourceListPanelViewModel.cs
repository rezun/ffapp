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
    private readonly ICropDetectionService? _cropDetectionService;

    // Crop analysis decodes video; run one source at a time even across overlapping add batches.
    private readonly SemaphoreSlim _cropAnalysisGate = new(1, 1);

    public ObservableCollection<SourceFileViewModel> Sources { get; } = [];

    [ObservableProperty]
    private SourceFileViewModel? _selectedSource;

    [ObservableProperty]
    private string _statusText = "No source files loaded.";

    /// <summary>All currently selected sources (driven by the view's SelectionChanged).</summary>
    public IReadOnlyList<SourceFileViewModel> SelectedSources { get; private set; } = [];

    public int ReadySourceCount => Sources.Count(source => source.MediaInfo is not null);

    public int SelectedReadySourceCount
    {
        get
        {
            var selectedCount = SelectedSources.Count(source => source.MediaInfo is not null);
            return selectedCount > 0
                ? selectedCount
                : SelectedSource?.MediaInfo is not null ? 1 : 0;
        }
    }

    public string MediaSummaryText =>
        SelectedSource?.MediaInfo is { } info ? FormatHelpers.BuildMediaSummaryText(info) : "";

    public SourceListPanelViewModel(
        IMediaProbeService mediaProbeService,
        IDialogService dialogService,
        IMessenger messenger,
        ICropDetectionService? cropDetectionService = null)
    {
        _mediaProbeService = mediaProbeService;
        _dialogService = dialogService;
        _messenger = messenger;
        _cropDetectionService = cropDetectionService;
    }

    /// <summary>
    /// Called from the view's SelectionChanged to sync multi-selection state.
    /// </summary>
    public void UpdateSelection(IReadOnlyList<SourceFileViewModel> selectedItems)
    {
        SelectedSources = selectedItems;
        OnPropertyChanged(nameof(SelectedReadySourceCount));
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedSourceChanged(SourceFileViewModel? value)
    {
        OnPropertyChanged(nameof(MediaSummaryText));
        OnPropertyChanged(nameof(SelectedReadySourceCount));
        RemoveSelectedCommand.NotifyCanExecuteChanged();

        if (value?.MediaInfo is { } mediaInfo)
        {
            _messenger.Send(new SourceLoadedMessage(mediaInfo));
        }
    }

    // Concurrent executions keep the add buttons usable while crop analysis of an
    // earlier batch is still running.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task AddFilesAsync()
    {
        var paths = await _dialogService.OpenFilesAsync("Select media files");
        if (paths is null) return;

        await AddPathsAndAnalyzeAsync(paths);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task AddFolderAsync()
    {
        var folder = await _dialogService.OpenFolderAsync("Select media folder");
        if (folder is null) return;

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => MediaFileExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        await AddPathsAndAnalyzeAsync(files);
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
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(f => MediaFileExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            else if (File.Exists(path))
            {
                files.Add(path);
            }
        }

        await AddPathsAndAnalyzeAsync(files);
    }

    /// <summary>
    /// Probes every path first so all sources become usable quickly, then looks for
    /// black bars in the new sources one by one.
    /// </summary>
    private async Task AddPathsAndAnalyzeAsync(IEnumerable<string> paths)
    {
        var added = new List<SourceFileViewModel>();
        foreach (var path in paths)
        {
            if (await AddSinglePathAsync(path) is { } source)
                added.Add(source);
        }

        foreach (var source in added)
            await SuggestCropAsync(source);
    }

    private async Task SuggestCropAsync(SourceFileViewModel source)
    {
        if (_cropDetectionService is null || source.MediaInfo is not { } mediaInfo)
            return;

        await _cropAnalysisGate.WaitAsync();
        try
        {
            // The source may have been removed while waiting for an earlier analysis.
            if (!Sources.Contains(source))
                return;

            source.IsAnalyzingCrop = true;
            source.SuggestedCrop = await _cropDetectionService.SuggestAsync(mediaInfo);
        }
        catch (CropDetectionException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Crop suggestion failed for {source.SourcePath}: {ex}");
        }
        finally
        {
            source.IsAnalyzingCrop = false;
            _cropAnalysisGate.Release();
        }
    }

    private async Task<SourceFileViewModel?> AddSinglePathAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;

        // Don't add duplicates
        if (Sources.Any(s => string.Equals(s.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
            return null;

        var vm = new SourceFileViewModel(path);
        vm.IsProbing = true;
        Sources.Add(vm);
        UpdateStatus();

        try
        {
            var mediaInfo = await _mediaProbeService.ProbeAsync(path, ct);
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
            UpdateStatus();
            return null;
        }

        UpdateStatus();
        return vm;
    }

    private void UpdateStatus()
    {
        OnPropertyChanged(nameof(ReadySourceCount));
        OnPropertyChanged(nameof(SelectedReadySourceCount));

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
