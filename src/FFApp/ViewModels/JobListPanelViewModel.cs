using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Helpers;
using FFApp.Services;

namespace FFApp.ViewModels;

public partial class JobListPanelViewModel : ObservableObject
{
    private readonly IQueueService _queueService;
    private readonly ICommandBuilder _commandBuilder;
    private readonly IValidationService _validationService;
    private readonly IDialogService _dialogService;
    private CancellationTokenSource? _cts;
    private int _nextJobId;

    /// <summary>Current job context from workspace (for the selected source).</summary>
    private JobContext? _currentContext;

    private readonly SourceListPanelViewModel _sourceList;
    private readonly VideoPreviewViewModel _videoPreview;
    private readonly OutputPathViewModel _outputPath;

    public ObservableCollection<JobItemViewModel> Jobs { get; } = [];

    [ObservableProperty]
    private JobItemViewModel? _selectedJob;

    /// <summary>All currently selected jobs (driven by the view's SelectionChanged).</summary>
    private IReadOnlyList<JobItemViewModel> _selectedJobs = [];

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _summaryText = "No jobs.";

    [ObservableProperty]
    private bool _isAnyJobRunning;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private double _statusPercentage;

    [ObservableProperty]
    private bool _hasValidationErrors;

    public JobItemDetailsViewModel Details { get; } = new();

    public JobListPanelViewModel(
        IQueueService queueService,
        ICommandBuilder commandBuilder,
        IValidationService validationService,
        IDialogService dialogService,
        SourceListPanelViewModel sourceList,
        VideoPreviewViewModel videoPreview,
        OutputPathViewModel outputPath)
    {
        _queueService = queueService;
        _commandBuilder = commandBuilder;
        _validationService = validationService;
        _dialogService = dialogService;
        _sourceList = sourceList;
        _videoPreview = videoPreview;
        _outputPath = outputPath;
    }

    public void UpdateJobContext(JobContext? context)
    {
        _currentContext = context;
        CreateJobFromSelectionCommand.NotifyCanExecuteChanged();
        CreateJobsForAllCommand.NotifyCanExecuteChanged();
    }

    public void UpdateHasValidationErrors(bool hasErrors)
    {
        HasValidationErrors = hasErrors;
        CreateJobFromSelectionCommand.NotifyCanExecuteChanged();
        CreateJobsForAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Called from the view's SelectionChanged to sync multi-selection state.
    /// </summary>
    public void UpdateSelection(IReadOnlyList<JobItemViewModel> selectedItems)
    {
        _selectedJobs = selectedItems;
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedJobChanged(JobItemViewModel? value)
    {
        Details.UpdateFromItem(value);
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    // ── Job Creation ──────────────────────────────────────────────

    private bool CanCreateJobFromSelection =>
        !HasValidationErrors
        && _currentContext is not null
        && (_sourceList.SelectedSources.Count > 0 || _sourceList.SelectedSource is not null);

    [RelayCommand(CanExecute = nameof(CanCreateJobFromSelection))]
    private async Task CreateJobFromSelectionAsync()
    {
        if (_currentContext is null) return;

        var selected = _sourceList.SelectedSources
            .Where(s => s.MediaInfo is not null).ToList();

        // Fall back to the focused source if multi-selection is empty
        if (selected.Count == 0 && _sourceList.SelectedSource?.MediaInfo is not null)
            selected = [_sourceList.SelectedSource];

        if (selected.Count == 0) return;

        var settings = _currentContext.Settings;
        var outputDirectory = _outputPath.GetEffectiveDirectory();

        // Save current source's trim/crop so it's up to date
        if (_sourceList.SelectedSource is { } currentSource)
            _videoPreview.SaveTrimCropToSource(currentSource);

        if (selected.Count == 1 && selected[0] == _sourceList.SelectedSource)
        {
            // Single focused source: use the full JobContext (user-configured output path)
            await CreateSingleJobFromContextAsync();
        }
        else
        {
            // Multiple selection: auto-name each, like "For All Sources" but scoped
            await CreateJobsForSourcesAsync(selected, settings, outputDirectory);
        }

        UpdateSummary();
    }

    private async Task CreateSingleJobFromContextAsync()
    {
        if (_currentContext is null) return;

        var mediaInfo = _currentContext.MediaInfo;
        var settings = _currentContext.Settings;

        var validation = _validationService.Validate(
            settings, mediaInfo, _currentContext.EffectiveDurationSeconds);
        if (validation.HasErrors)
        {
            await _dialogService.ShowErrorAsync(
                "Validation Error",
                string.Join(Environment.NewLine, validation.Errors.Select(e => e.Message)));
            return;
        }

        var job = FormatHelpers.BuildJobDefinition(_currentContext);

        IReadOnlyList<GeneratedCommand> commands;
        try
        {
            commands = _commandBuilder.Build(job, mediaInfo);
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Command Error", ex.Message);
            return;
        }

        var queuedJob = new QueuedJob(job, mediaInfo, [.. commands], validation);
        var id = $"job-{++_nextJobId}";
        var label = Path.GetFileName(mediaInfo.SourcePath);
        Jobs.Add(new JobItemViewModel(id, mediaInfo, queuedJob, label));
    }

    private bool CanCreateJobsForAll =>
        !HasValidationErrors
        && _sourceList.Sources.Count > 0
        && _sourceList.Sources.Any(s => s.MediaInfo is not null);

    [RelayCommand(CanExecute = nameof(CanCreateJobsForAll))]
    private async Task CreateJobsForAllAsync()
    {
        if (_currentContext is null) return;

        var settings = _currentContext.Settings;
        var outputDirectory = _outputPath.GetEffectiveDirectory();

        // Save current source's trim/crop so it's up to date
        if (_sourceList.SelectedSource is { } currentSource)
            _videoPreview.SaveTrimCropToSource(currentSource);

        var sourcesWithMedia = _sourceList.Sources.Where(s => s.MediaInfo is not null).ToList();
        await CreateJobsForSourcesAsync(sourcesWithMedia, settings, outputDirectory);
        UpdateSummary();
    }

    private async Task CreateJobsForSourcesAsync(
        IReadOnlyList<SourceFileViewModel> sources,
        OutputSettings settings,
        string? outputDirectory)
    {
        var errorMessages = new List<string>();

        foreach (var source in sources)
        {
            var mediaInfo = source.MediaInfo!;

            // Get effective trim/crop (respects "apply to all" flags)
            var (trimIn, trimOut, crop) = _videoPreview.GetEffectiveTrimCrop(source);

            // Auto-name output (isAutoName: true enables collision avoidance)
            var outputPath = DefaultCommandBuilder.SuggestOutputPath(
                source.SourcePath, settings.Container, outputDirectory,
                outputFilename: null, isAutoName: true);

            var job = FormatHelpers.BuildJobDefinition(
                source.SourcePath, outputPath, settings, trimIn, trimOut, crop);

            var validation = _validationService.Validate(settings, mediaInfo);

            if (validation.HasErrors)
            {
                errorMessages.Add($"{source.DisplayLabel}: {string.Join("; ", validation.Errors.Select(e => e.Message))}");
                continue;
            }

            IReadOnlyList<GeneratedCommand> commands;
            try
            {
                commands = _commandBuilder.Build(job, mediaInfo);
            }
            catch
            {
                commands = [];
            }

            var queuedJob = new QueuedJob(job, mediaInfo, [.. commands], validation);
            var id = $"job-{++_nextJobId}";
            Jobs.Add(new JobItemViewModel(id, mediaInfo, queuedJob, source.DisplayLabel));
        }

        if (errorMessages.Count > 0)
        {
            await _dialogService.ShowErrorAsync(
                "Some files skipped",
                string.Join(Environment.NewLine, errorMessages));
        }
    }

    // ── Job Execution ─────────────────────────────────────────────

    private bool CanStart => !IsRunning && Jobs.Any(j => j.State == JobState.Pending);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsRunning || !Jobs.Any(j => j.State == JobState.Pending))
            return;

        // Reset failed/canceled jobs so they re-run
        foreach (var job in Jobs)
        {
            if (job.State is JobState.Canceled or JobState.Failed)
                job.ResetToPending();
        }

        // Enqueue all pending jobs into the queue service
        var pendingJobs = Jobs.Where(j => j.State == JobState.Pending).ToList();
        var toEnqueue = pendingJobs.Select(j => (j.Id, j.QueuedJob)).ToList();
        _queueService.Enqueue(toEnqueue);

        IsRunning = true;
        IsPaused = false;
        IsAnyJobRunning = true;
        NotifyExecutionCommands();

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            await _queueService.ExecuteAllAsync(
                itemStarted: queueItem =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var vm = FindJobById(queueItem.Id);
                        if (vm is not null)
                        {
                            vm.State = JobState.Running;
                            UpdateStatusFromRunningJob(vm);
                        }
                    });
                },
                itemProgress: queueItem =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var vm = FindJobById(queueItem.Id);
                        if (vm is not null && queueItem.Progress is { } progress)
                        {
                            vm.UpdateProgress(progress);
                            UpdateStatusFromRunningJob(vm);
                        }
                    });
                },
                itemFinished: queueItem =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var vm = FindJobById(queueItem.Id);
                        if (vm is not null)
                        {
                            vm.State = queueItem.State;
                            if (queueItem.Result is { } result)
                                vm.UpdateResult(result);
                        }
                        // Track next running job for status bar
                        var nextRunning = Jobs.FirstOrDefault(j => j.State == JobState.Running);
                        if (nextRunning is not null)
                            UpdateStatusFromRunningJob(nextRunning);
                    });
                },
                logCallback: (queueItem, line) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var vm = FindJobById(queueItem.Id);
                        vm?.AppendLog(line);
                    });
                },
                ct: _cts.Token);
        }
        catch (OperationCanceledException)
        {
            foreach (var job in Jobs)
            {
                if (job.State == JobState.Pending)
                    job.State = JobState.Canceled;
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Execution Error", ex.Message);
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            IsAnyJobRunning = false;
            StatusText = "";
            StatusPercentage = 0;
            NotifyExecutionCommands();
            _cts?.Dispose();
            _cts = null;
            UpdateSummary();
        }
    }

    [RelayCommand]
    private void PauseResume()
    {
        if (!IsRunning) return;

        if (IsPaused)
        {
            _queueService.Resume();
            IsPaused = false;
        }
        else
        {
            _queueService.Pause();
            IsPaused = true;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _queueService.CancelActive();
        _cts?.Cancel();
    }

    // ── List Management ───────────────────────────────────────────

    private bool CanRemoveSelected =>
        _selectedJobs.Count > 0 && _selectedJobs.Any(j => j.State != JobState.Running);

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private void RemoveSelected()
    {
        // Only remove non-running jobs
        var toRemove = _selectedJobs.Where(j => j.State != JobState.Running).ToList();
        if (toRemove.Count == 0) return;

        var ids = toRemove.Select(j => j.Id).ToList();
        _queueService.Remove(ids);

        foreach (var job in toRemove)
            Jobs.Remove(job);

        SelectedJob = null;
        _selectedJobs = [];
        UpdateSummary();
    }

    private bool CanClearFinished =>
        Jobs.Any(j => j.State is JobState.Completed or JobState.Failed or JobState.Canceled);

    [RelayCommand(CanExecute = nameof(CanClearFinished))]
    private void ClearFinished()
    {
        var finished = Jobs.Where(j => j.State is JobState.Completed or JobState.Failed or JobState.Canceled).ToList();
        var ids = finished.Select(j => j.Id).ToList();
        if (ids.Count > 0)
            _queueService.Remove(ids);

        foreach (var job in finished)
            Jobs.Remove(job);

        if (SelectedJob is not null && !Jobs.Contains(SelectedJob))
            SelectedJob = null;

        UpdateSummary();
    }

    private bool CanClearAll => Jobs.Count > 0 && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanClearAll))]
    private void ClearAll()
    {

        var ids = Jobs.Select(j => j.Id).ToList();
        if (ids.Count > 0)
            _queueService.Remove(ids);

        Jobs.Clear();
        SelectedJob = null;
        UpdateSummary();
    }

    // ── Helpers ────────────────────────────────────────────────────

    private JobItemViewModel? FindJobById(string id) =>
        Jobs.FirstOrDefault(j => j.Id == id);

    private void UpdateSummary()
    {
        if (Jobs.Count == 0)
        {
            SummaryText = "No jobs.";
        }
        else
        {
            var pending = Jobs.Count(j => j.State == JobState.Pending);
            var completed = Jobs.Count(j => j.State == JobState.Completed);
            var failed = Jobs.Count(j => j.State == JobState.Failed);
            SummaryText = $"{Jobs.Count} jobs: {pending} pending, {completed} done, {failed} failed";
        }

        NotifyExecutionCommands();
    }

    private void UpdateStatusFromRunningJob(JobItemViewModel job)
    {
        var filename = Path.GetFileName(job.SourcePath);
        var progress = job.ProgressText;
        StatusText = string.IsNullOrEmpty(progress)
            ? $"Processing: {filename}"
            : $"Processing: {filename}  |  {progress}";
        StatusPercentage = job.Percentage;
    }

    private void NotifyExecutionCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        ClearFinishedCommand.NotifyCanExecuteChanged();
        ClearAllCommand.NotifyCanExecuteChanged();
        CreateJobFromSelectionCommand.NotifyCanExecuteChanged();
        CreateJobsForAllCommand.NotifyCanExecuteChanged();
    }
}
