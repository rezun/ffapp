using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Helpers;
using FFApp.Infrastructure;
using FFApp.Services;

namespace FFApp.ViewModels;

public partial class JobListPanelViewModel : ObservableObject
{
    private readonly IQueueService _queueService;
    private readonly ICommandBuilder _commandBuilder;
    private readonly IValidationService _validationService;
    private readonly IDialogService _dialogService;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _cts;
    private int _nextJobId;

    /// <summary>Current job context from workspace (for the selected source).</summary>
    private JobContext? _currentContext;

    private readonly SourceListPanelViewModel _sourceList;
    private readonly VideoPreviewViewModel _videoPreview;

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

    /// <summary>Estimated time until all queued jobs are encoded; empty when no estimate applies.</summary>
    [ObservableProperty]
    private string _queueEstimateText = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private double _statusPercentage;

    [ObservableProperty]
    private bool _hasValidationErrors;

    public bool HasJobs => Jobs.Count > 0;

    public bool HasQueueableSelection => _sourceList.SelectedReadySourceCount > 0;

    public string AddSelectionLabel => _sourceList.SelectedReadySourceCount switch
    {
        0 => "Add selected",
        1 => "Add selected file",
        var count => $"Add {count} selected files",
    };

    public string AddAllLabel => _sourceList.ReadySourceCount switch
    {
        0 or 1 => "Add all files",
        var count => $"Add all {count} files",
    };

    public string EmptyQueueHint => _sourceList.SelectedReadySourceCount switch
    {
        0 when _sourceList.ReadySourceCount == 0 =>
            "Add source files and configure their output settings first.",
        0 => "Select source files to add them with the current output settings.",
        1 => "The selected source will use the current output settings.",
        _ => "The selected sources will use the current output settings.",
    };

    public JobItemDetailsViewModel Details { get; }

    public JobListPanelViewModel(
        IQueueService queueService,
        ICommandBuilder commandBuilder,
        IValidationService validationService,
        IDialogService dialogService,
        SourceListPanelViewModel sourceList,
        VideoPreviewViewModel videoPreview,
        TimeProvider timeProvider)
    {
        _queueService = queueService;
        _timeProvider = timeProvider;
        _commandBuilder = commandBuilder;
        _validationService = validationService;
        _dialogService = dialogService;
        _sourceList = sourceList;
        _videoPreview = videoPreview;
        Details = new JobItemDetailsViewModel(RenameJobOutputAsync, _dialogService.OpenWithDefaultAppAsync);

        Jobs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasJobs));
        _sourceList.PropertyChanged += OnSourceListPropertyChanged;
    }

    private void OnSourceListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SourceListPanelViewModel.ReadySourceCount)
            or nameof(SourceListPanelViewModel.SelectedReadySourceCount))
        {
            OnPropertyChanged(nameof(HasQueueableSelection));
            OnPropertyChanged(nameof(AddSelectionLabel));
            OnPropertyChanged(nameof(AddAllLabel));
            OnPropertyChanged(nameof(EmptyQueueHint));
            CreateJobFromSelectionCommand.NotifyCanExecuteChanged();
            CreateJobsForAllCommand.NotifyCanExecuteChanged();
        }
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
        && _sourceList.SelectedReadySourceCount > 0;

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

        // Save current source's trim/crop so it's up to date
        if (_sourceList.SelectedSource is { } currentSource)
            _videoPreview.SaveTrimCropToSource(currentSource);

        if (selected.Count == 1 && selected[0] == _sourceList.SelectedSource)
        {
            // Single focused source: use the full JobContext (its trim, crop and audio tracks)
            await CreateSingleJobFromContextAsync();
        }
        else
        {
            // Multiple selection: same naming as "For All Sources", but scoped
            await CreateJobsForSourcesAsync(selected, _currentContext);
        }

        UpdateSummary();
    }

    private async Task CreateSingleJobFromContextAsync()
    {
        if (_currentContext is null) return;

        var mediaInfo = _currentContext.MediaInfo;
        var settings = _currentContext.Settings;

        var validation = _validationService.Validate(
            settings,
            TrackSelection.Apply(mediaInfo, _currentContext.SelectedAudioStreamIndices, _currentContext.SelectedSubtitleStreamIndices),
            _currentContext.EffectiveDurationSeconds);
        if (validation.HasErrors)
        {
            await _dialogService.ShowErrorAsync(
                "Validation error",
                string.Join(Environment.NewLine, validation.Errors.Select(e => e.Message)));
            return;
        }

        var job = FormatHelpers.BuildJobDefinition(_currentContext, ReservedOutputPaths());

        IReadOnlyList<GeneratedCommand> commands;
        try
        {
            commands = _commandBuilder.Build(job, mediaInfo);
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Command error", ex.Message);
            return;
        }

        var queuedJob = new QueuedJob(job, mediaInfo, [.. commands], validation);
        var id = $"job-{++_nextJobId}";
        var label = Path.GetFileName(mediaInfo.SourcePath);
        AddJob(new JobItemViewModel(id, mediaInfo, queuedJob, label), queuedJob);
    }

    private bool CanCreateJobsForAll =>
        !HasValidationErrors
        && _currentContext is not null
        && _sourceList.ReadySourceCount > 0;

    [RelayCommand(CanExecute = nameof(CanCreateJobsForAll))]
    private async Task CreateJobsForAllAsync()
    {
        if (_currentContext is null) return;

        // Save current source's trim/crop so it's up to date
        if (_sourceList.SelectedSource is { } currentSource)
            _videoPreview.SaveTrimCropToSource(currentSource);

        var sourcesWithMedia = _sourceList.Sources.Where(s => s.MediaInfo is not null).ToList();
        await CreateJobsForSourcesAsync(sourcesWithMedia, _currentContext);
        UpdateSummary();
    }

    private async Task CreateJobsForSourcesAsync(
        IReadOnlyList<SourceFileViewModel> sources,
        JobContext context)
    {
        var settings = context.Settings;
        var errorMessages = new List<string>();
        var reservedOutputPaths = ReservedOutputPaths();

        foreach (var source in sources)
        {
            var mediaInfo = source.MediaInfo!;

            // Get effective trim/crop (respects "apply to all" flags)
            var (trimIn, trimOut, crop) = _videoPreview.GetEffectiveTrimCrop(source);

            // Auto-name output (isAutoName: true enables collision avoidance)
            var outputPath = DefaultCommandBuilder.SuggestOutputPath(
                source.SourcePath, settings.Container, context.OutputDirectory,
                OutputNaming.AutoStem(source.SourcePath, context.OutputPresetName),
                isAutoName: true, reservedOutputPaths: reservedOutputPaths);

            var job = FormatHelpers.BuildJobDefinition(
                source.SourcePath, outputPath, settings, trimIn, trimOut, crop,
                source.SelectedAudioStreamIndices, source.SelectedSubtitleStreamIndices);

            var validation = _validationService.Validate(settings,
                TrackSelection.Apply(mediaInfo, source.SelectedAudioStreamIndices, source.SelectedSubtitleStreamIndices));

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
            reservedOutputPaths.Add(outputPath);
            AddJob(new JobItemViewModel(id, mediaInfo, queuedJob, source.DisplayLabel), queuedJob);
        }

        if (errorMessages.Count > 0)
        {
            await _dialogService.ShowErrorAsync(
                "Some files skipped",
                string.Join(Environment.NewLine, errorMessages));
        }
    }

    // ── Output Rename ─────────────────────────────────────────────

    /// <summary>
    /// Gives a queued job a new output file name (same folder and extension) and rebuilds its
    /// commands. Only jobs that have not started or will re-run can be renamed.
    /// </summary>
    internal async Task RenameJobOutputAsync(JobItemViewModel job, string requestedStem)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(requestedStem);
        if (!job.CanRenameOutput)
            return;

        var stem = requestedStem.Trim();
        if (!OutputNaming.IsValidStem(stem))
        {
            await _dialogService.ShowErrorAsync(
                "Invalid file name",
                "Enter a file name without a folder or any of these characters: / \\ : * ? \" < > |");
            return;
        }

        var current = job.QueuedJob.Job.OutputPath;
        var renamed = Path.Combine(
            Path.GetDirectoryName(current) ?? "",
            string.Concat(stem, Path.GetExtension(current)));
        if (OutputNaming.PathComparer.Equals(renamed, current))
            return;

        var renamedFullPath = Path.GetFullPath(renamed);
        if (OutputNaming.PathComparer.Equals(renamedFullPath, Path.GetFullPath(job.SourcePath)))
        {
            await _dialogService.ShowErrorAsync(
                "File name in use", "The output cannot replace its source file.");
            return;
        }

        if (Jobs.Any(other => other != job
            && OutputNaming.PathComparer.Equals(Path.GetFullPath(other.QueuedJob.Job.OutputPath), renamedFullPath)))
        {
            await _dialogService.ShowErrorAsync(
                "File name in use", $"Another job in the queue already writes {Path.GetFileName(renamed)}.");
            return;
        }

        var definition = job.QueuedJob.Job with { OutputPath = renamed };
        IReadOnlyList<GeneratedCommand> commands;
        try
        {
            commands = _commandBuilder.Build(definition, job.MediaInfo);
        }
        catch (CommandBuildException ex)
        {
            await _dialogService.ShowErrorAsync("Command error", ex.Message);
            return;
        }

        var updated = job.QueuedJob with { Job = definition, Commands = [.. commands] };

        // The queue refuses jobs that started while the user was typing
        if (_queueService.Replace(job.Id, updated))
            job.QueuedJob = updated;
    }

    // ── Job Execution ─────────────────────────────────────────────

    private bool CanStart => !IsRunning && Jobs.Any(j => j.State == JobState.Pending);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsRunning || !Jobs.Any(j => j.State == JobState.Pending))
            return;

        // Reset failed/canceled jobs so they re-run. Items are already enqueued at creation
        // time, so here we only flip VM + queue state back to Pending.
        var resetIds = new List<string>();
        foreach (var job in Jobs)
        {
            if (job.State is JobState.Canceled or JobState.Failed)
            {
                job.ResetToPending();
                resetIds.Add(job.Id);
            }
        }
        if (resetIds.Count > 0)
            _queueService.ResetToPending(resetIds);

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
                            vm.LogFilePath = queueItem.LogPath;
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
                            UpdateQueueEstimate();
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
                            else if (queueItem.State == JobState.Failed)
                                vm.UpdateFailure(queueItem.FailureMessage);
                        }
                        // Track next running job for status bar
                        var nextRunning = Jobs.FirstOrDefault(j => j.State == JobState.Running);
                        if (nextRunning is not null)
                            UpdateStatusFromRunningJob(nextRunning);
                        UpdateSummary();
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
            await _dialogService.ShowErrorAsync("Execution error", ex.Message);
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
    private async Task PauseResume()
    {
        if (!IsRunning) return;

        try
        {
            if (IsPaused)
                _queueService.Resume();
            else
                _queueService.Pause();
            IsPaused = _queueService.IsPaused;
            var activeJob = Jobs.FirstOrDefault(job => job.State == JobState.Running);
            if (activeJob is not null)
                UpdateStatusFromRunningJob(activeJob);
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Pause / resume error", ex.Message);
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

    private HashSet<string> ReservedOutputPaths() =>
        [.. Jobs.Select(job => job.QueuedJob.Job.OutputPath)];

    /// <summary>
    /// Adds a job to the visible list AND enqueues it in the queue service so that jobs added
    /// mid-run are picked up by the running execution loop. Jobs added while paused also sit
    /// as Pending and are processed after Resume.
    /// </summary>
    private void AddJob(JobItemViewModel vm, QueuedJob queuedJob)
    {
        Jobs.Add(vm);
        _queueService.Enqueue([(vm.Id, queuedJob)]);
    }

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

        UpdateQueueEstimate();
        NotifyExecutionCommands();
    }

    private void UpdateQueueEstimate()
    {
        var entries = Jobs.Select(job => new QueueEstimateEntry(
                job.State,
                DefaultJobExecutionService.EffectiveOutputDuration(job.QueuedJob.Job, job.MediaInfo),
                job.QueuedJob.Commands.Count(command => command.Protocol == ProcessProtocol.Ffmpeg),
                job.LastProgress,
                job.Result?.ElapsedSeconds))
            .ToList();

        if (!IsRunning || QueueTimeEstimator.EstimateRemainingSeconds(entries) is not { } remaining)
        {
            QueueEstimateText = "";
            return;
        }

        var finishAt = _timeProvider.GetLocalNow().AddSeconds(remaining);
        var finishFormat = finishAt.Date == _timeProvider.GetLocalNow().Date ? "t" : "g";
        QueueEstimateText =
            $"All jobs: ~{FormatHelpers.FormatCompactDuration(remaining)} left, done around {finishAt.ToString(finishFormat, CultureInfo.CurrentCulture)}";
    }

    private void UpdateStatusFromRunningJob(JobItemViewModel job)
    {
        var filename = Path.GetFileName(job.SourcePath);
        var progress = job.ProgressText;
        var activity = IsPaused ? "Paused" : "Processing";
        StatusText = string.IsNullOrEmpty(progress)
            ? $"{activity}: {filename}"
            : $"{activity}: {filename}  |  {progress}";
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
