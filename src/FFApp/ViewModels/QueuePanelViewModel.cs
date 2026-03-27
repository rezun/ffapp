using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Helpers;
using FFApp.Services;

namespace FFApp.ViewModels;

public partial class QueuePanelViewModel : ObservableObject
{
    private static readonly string[] MediaFileExtensions =
    [
        ".aac", ".avi", ".flac", ".m4a", ".mkv", ".mov",
        ".mp3", ".mp4", ".mpeg", ".mpg", ".ogg", ".opus",
        ".wav", ".webm",
    ];

    private readonly IQueueService _queueService;
    private readonly IMediaProbeService _mediaProbeService;
    private readonly ICommandBuilder _commandBuilder;
    private readonly IValidationService _validationService;
    private readonly IDialogService _dialogService;
    private CancellationTokenSource? _cts;
    private int _nextLocalId;

    public ObservableCollection<QueueItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private QueueItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _summaryText = "Queue is empty.";

    public QueueItemDetailsViewModel Details { get; } = new();

    public QueuePanelViewModel(
        IQueueService queueService,
        IMediaProbeService mediaProbeService,
        ICommandBuilder commandBuilder,
        IValidationService validationService,
        IDialogService dialogService)
    {
        _queueService = queueService;
        _mediaProbeService = mediaProbeService;
        _commandBuilder = commandBuilder;
        _validationService = validationService;
        _dialogService = dialogService;
    }

    partial void OnSelectedItemChanged(QueueItemViewModel? value)
    {
        Details.UpdateFromItem(value);
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemoveSelected => SelectedItem is not null;
    private bool CanStartQueue => !IsRunning && Items.Count > 0;

    private OutputSettings? _currentOutputSettings;
    private SourceMediaInfo? _currentMediaInfo;
    private string? _outputDirectory;

    public void UpdateCurrentContext(OutputSettings settings) =>
        _currentOutputSettings = settings;

    public void UpdateCurrentContext(SourceMediaInfo mediaInfo) =>
        _currentMediaInfo = mediaInfo;

    public void UpdateOutputDirectory(string? directory) =>
        _outputDirectory = directory;

    [RelayCommand]
    private void AddCurrentSource()
    {
        if (_currentMediaInfo is null)
            return;

        AddSource(_currentMediaInfo);
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var paths = await _dialogService.OpenFilesAsync("Select media files");
        if (paths is null)
            return;

        foreach (var path in paths)
        {
            try
            {
                var mediaInfo = await _mediaProbeService.ProbeAsync(path);
                AddSource(mediaInfo);
            }
            catch (Exception ex)
            {
                await _dialogService.ShowErrorAsync("Probe Failed", $"Could not probe {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var folder = await _dialogService.OpenFolderAsync("Select media folder");
        if (folder is null)
            return;

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => MediaFileExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            try
            {
                var mediaInfo = await _mediaProbeService.ProbeAsync(path);
                AddSource(mediaInfo);
            }
            catch
            {
                // Skip files that can't be probed
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private void RemoveSelected()
    {
        if (SelectedItem is null)
            return;

        // If already enqueued in the service, remove there too
        if (SelectedItem.QueuedJob is not null)
            _queueService.Remove([SelectedItem.Id]);

        Items.Remove(SelectedItem);
        SelectedItem = null;
        UpdateSummary();
    }

    [RelayCommand(CanExecute = nameof(CanClearList))]
    private void ClearList()
    {
        var idsToRemove = Items.Where(i => i.QueuedJob is not null).Select(i => i.Id).ToList();
        if (idsToRemove.Count > 0)
            _queueService.Remove(idsToRemove);

        Items.Clear();
        SelectedItem = null;
        UpdateSummary();
    }

    [RelayCommand(CanExecute = nameof(CanClearFinished))]
    private void ClearFinished()
    {
        var finished = Items.Where(i => i.State is JobState.Completed or JobState.Failed or JobState.Canceled).ToList();
        var idsToRemove = finished.Where(i => i.QueuedJob is not null).Select(i => i.Id).ToList();
        if (idsToRemove.Count > 0)
            _queueService.Remove(idsToRemove);

        foreach (var item in finished)
            Items.Remove(item);

        if (SelectedItem is not null && !Items.Contains(SelectedItem))
            SelectedItem = null;

        UpdateSummary();
    }

    private bool CanClearList => !IsRunning && Items.Count > 0;
    private bool CanClearFinished => !IsRunning && Items.Any(i => i.State is JobState.Completed or JobState.Failed or JobState.Canceled);

    [RelayCommand(CanExecute = nameof(CanStartQueue))]
    private async Task StartQueueAsync()
    {
        if (IsRunning || Items.Count == 0)
            return;

        var settings = _currentOutputSettings ?? new OutputSettings();

        // Reset canceled/failed items so they can be re-run
        foreach (var item in Items)
        {
            if (item.State is JobState.Canceled or JobState.Failed)
            {
                // Remove from service if previously enqueued
                if (item.QueuedJob is not null)
                    _queueService.Remove([item.Id]);

                item.ResetToPending();
            }
        }

        // Resolve all pending items with current settings before execution
        var pendingItems = Items.Where(i => i.State == JobState.Pending).ToList();
        foreach (var item in pendingItems)
        {
            var outputPath = DefaultCommandBuilder.SuggestOutputPath(
                item.SourcePath, settings.Container,
                _outputDirectory);

            var job = FormatHelpers.BuildJobDefinition(
                item.SourcePath, outputPath, settings);

            var validation = _validationService.Validate(settings, item.MediaInfo);

            IReadOnlyList<GeneratedCommand> commands;
            try
            {
                commands = _commandBuilder.Build(job, item.MediaInfo);
            }
            catch
            {
                commands = [];
            }

            var queuedJob = new QueuedJob(job, item.MediaInfo, [.. commands], validation);
            item.ResolveWithSettings(queuedJob);

            _queueService.Enqueue([(item.Id, queuedJob)]);
        }

        // Update details if a pending item is selected (now resolved)
        if (SelectedItem is not null && pendingItems.Contains(SelectedItem))
            Details.UpdateFromItem(SelectedItem);

        IsRunning = true;
        StartQueueCommand.NotifyCanExecuteChanged();
        CancelQueueCommand.NotifyCanExecuteChanged();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            await _queueService.ExecuteAllAsync(
                itemStarted: queueItem =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var vm = FindItemById(queueItem.Id);
                        if (vm is not null)
                            vm.State = JobState.Running;
                    });
                },
                itemProgress: queueItem =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var vm = FindItemById(queueItem.Id);
                        if (vm is not null && queueItem.Progress is { } progress)
                            vm.UpdateProgress(progress);
                    });
                },
                itemFinished: queueItem =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var vm = FindItemById(queueItem.Id);
                        if (vm is not null)
                        {
                            vm.State = queueItem.State;
                            if (queueItem.Result is { } result)
                                vm.UpdateResult(result);
                        }
                    });
                },
                logCallback: (queueItem, line) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var vm = FindItemById(queueItem.Id);
                        vm?.AppendLog(line);
                    });
                },
                ct: _cts.Token);
        }
        catch (OperationCanceledException)
        {
            foreach (var item in Items)
            {
                if (item.State == JobState.Pending)
                    item.State = JobState.Canceled;
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Queue Error", ex.Message);
        }
        finally
        {
            IsRunning = false;
            StartQueueCommand.NotifyCanExecuteChanged();
            CancelQueueCommand.NotifyCanExecuteChanged();
            _cts?.Dispose();
            _cts = null;
            UpdateSummary();
        }
    }

    [RelayCommand]
    private void CancelQueue()
    {
        _queueService.CancelActive();
        _cts?.Cancel();
    }

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
                {
                    try
                    {
                        var mediaInfo = await _mediaProbeService.ProbeAsync(file);
                        AddSource(mediaInfo);
                    }
                    catch
                    {
                        // Skip files that can't be probed
                    }
                }
            }
            else if (File.Exists(path))
            {
                try
                {
                    var mediaInfo = await _mediaProbeService.ProbeAsync(path);
                    AddSource(mediaInfo);
                }
                catch
                {
                    // Skip files that can't be probed
                }
            }
        }
    }

    private void AddSource(SourceMediaInfo mediaInfo)
    {
        var id = $"queue-{++_nextLocalId}";
        var label = Path.GetFileName(mediaInfo.SourcePath);
        var vm = new QueueItemViewModel(id, mediaInfo, label);
        Items.Add(vm);
        UpdateSummary();
    }

    private QueueItemViewModel? FindItemById(string id) =>
        Items.FirstOrDefault(i => i.Id == id);

    private void UpdateSummary()
    {
        if (Items.Count == 0)
        {
            SummaryText = "Queue is empty.";
        }
        else
        {
            var pending = Items.Count(i => i.State == JobState.Pending);
            var completed = Items.Count(i => i.State == JobState.Completed);
            var failed = Items.Count(i => i.State == JobState.Failed);

            SummaryText = $"{Items.Count} items: {pending} pending, {completed} completed, {failed} failed";
        }

        StartQueueCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        ClearListCommand.NotifyCanExecuteChanged();
        ClearFinishedCommand.NotifyCanExecuteChanged();
    }
}
