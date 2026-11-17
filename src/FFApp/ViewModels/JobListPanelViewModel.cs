using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Helpers;
using FFApp.Infrastructure;
using FFApp.Remote;
using FFApp.Remote.Contracts;
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
    private readonly RemoteAgentConnection? _agent;
    private readonly RemoteJobExecutor? _remoteExecutor;
    private readonly INotificationService? _notifications;
    private readonly IMessenger? _messenger;

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
        TimeProvider timeProvider,
        RemoteAgentConnection? agent = null,
        RemoteJobExecutor? remoteExecutor = null,
        INotificationService? notifications = null,
        IMessenger? messenger = null)
    {
        _queueService = queueService;
        _timeProvider = timeProvider;
        _commandBuilder = commandBuilder;
        _validationService = validationService;
        _dialogService = dialogService;
        _sourceList = sourceList;
        _videoPreview = videoPreview;
        Details = new JobItemDetailsViewModel(RenameJobOutputAsync, _dialogService.OpenWithDefaultAppAsync);

        _agent = agent;
        _remoteExecutor = remoteExecutor;
        _notifications = notifications;
        _messenger = messenger;

        Jobs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasJobs));
        _sourceList.PropertyChanged += OnSourceListPropertyChanged;

        if (_agent is not null)
        {
            // Connection events arrive on background threads.
            _agent.StatusChanged += _ => Dispatcher.UIThread.Post(UpdateServerState);
            _agent.LoadChanged += _ => Dispatcher.UIThread.Post(UpdateServerState);
            _agent.UpdateChanged += _ => Dispatcher.UIThread.Post(UpdateServerState);
            UpdateServerState();
        }
    }

    // ── Server target ─────────────────────────────────────────────

    /// <summary>Where new jobs run: this computer or the server agent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerTarget))]
    [NotifyPropertyChangedFor(nameof(IsLocalTarget))]
    private ExecutionTarget _target = ExecutionTarget.Local;

    // Segmented toggle: clicking the selected segment again keeps it selected.
    public bool IsServerTarget
    {
        get => Target == ExecutionTarget.Server;
        set
        {
            if (value && IsServerAvailable)
            {
                _targetChosenByUser = true;
                Target = ExecutionTarget.Server;
            }
            OnPropertyChanged();
        }
    }

    public bool IsLocalTarget
    {
        get => Target == ExecutionTarget.Local;
        set
        {
            if (value)
            {
                _targetChosenByUser = true;
                Target = ExecutionTarget.Local;
            }
            OnPropertyChanged();
        }
    }

    /// <summary>The server toggle is usable: an agent is set up, reachable and compatible.</summary>
    [ObservableProperty]
    private bool _isServerAvailable;

    /// <summary>Explains the server toggle: why it is disabled, or the current server load.</summary>
    [ObservableProperty]
    private string _serverStatusText = "No server agent is set up. Add one in Settings → Server agent.";

    /// <summary>Non-blocking hint, such as an agent version that differs from the app.</summary>
    [ObservableProperty]
    private string _serverHintText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAgentCommand))]
    private bool _offersAgentUpdate;

    public string UpdateAgentLabel => $"Update agent to {AppVersion.Current}";

    public ObservableCollection<ResultOption> ResultOptions { get; } = [];

    [ObservableProperty]
    private ResultOption? _selectedResultOption;

    private RemoteAgentSettings _remoteDefaults = new();
    private bool _targetChosenByUser;
    private bool _resultChosenByUser;
    private bool _updatingResultOptions;

    partial void OnSelectedResultOptionChanged(ResultOption? value)
    {
        if (!_updatingResultOptions && value is not null)
            _resultChosenByUser = true;
    }

    /// <summary>The result choice from Settings → Server agent, once the agent lists its folders.</summary>
    private ResultOption? DefaultResultOption() =>
        ResultOptions.FirstOrDefault(option => option.Mode == _remoteDefaults.DefaultResultMode
            && (option.Mode == ResultMode.Download
                || string.Equals(option.Root, _remoteDefaults.DefaultServerOutputRoot, StringComparison.Ordinal)));

    /// <summary>Applies the defaults from Settings → Server agent.</summary>
    public void ApplyRemoteDefaults(RemoteAgentSettings defaults)
    {
        _remoteDefaults = defaults ?? new RemoteAgentSettings();
        _targetChosenByUser = false;
        _resultChosenByUser = false;
        Target = _remoteDefaults.DefaultTarget == ExecutionTarget.Server && IsServerAvailable
            ? ExecutionTarget.Server : ExecutionTarget.Local;
        UpdateServerState();
    }

    partial void OnTargetChanged(ExecutionTarget value)
    {
        SendValidationTarget();
        CreateJobFromSelectionCommand.NotifyCanExecuteChanged();
        CreateJobsForAllCommand.NotifyCanExecuteChanged();
    }

    private void SendValidationTarget()
    {
        var info = _agent?.Status.Info;
        _messenger?.Send(new ValidationTargetChangedMessage(Target == ExecutionTarget.Server,
            info?.Capabilities, info?.HasMkvpropedit ?? false));
    }

    private void UpdateServerState()
    {
        if (_agent is null)
            return;
        var status = _agent.Status;
        IsServerAvailable = status.IsReady;
        OffersAgentUpdate = status.OffersUpdate && status.Info is not null;
        ServerHintText = status.IsReady && status.OffersUpdate ? status.Message : "";

        if (!status.IsReady)
            ServerStatusText = status.Message;
        else if (_agent.Load is { } load)
            ServerStatusText = DescribeLoad(load, _timeProvider.GetUtcNow());
        else
            ServerStatusText = status.Message;
        if (_agent.Update is { State: not UpdateStateKind.Idle } update)
            ServerHintText = DescribeUpdate(update);

        var roots = status.Info?.OutputRoots ?? [];
        var options = new List<ResultOption> { new(OperatingSystem.IsMacOS() ? "To this Mac" : "To this computer", ResultMode.Download, null) };
        options.AddRange(roots.Select(root => new ResultOption($"On the server: {root}", ResultMode.KeepOnServer, root)));
        _updatingResultOptions = true;
        try
        {
            var selected = SelectedResultOption;
            if (!options.SequenceEqual(ResultOptions))
            {
                ResultOptions.Clear();
                foreach (var option in options)
                    ResultOptions.Add(option);
            }
            SelectedResultOption = (_resultChosenByUser ? ResultOptions.FirstOrDefault(option => option == selected) : null)
                ?? DefaultResultOption()
                ?? ResultOptions.FirstOrDefault(option => option == selected)
                ?? ResultOptions.FirstOrDefault();
        }
        finally
        {
            _updatingResultOptions = false;
        }

        // The default target applies once the agent is reachable, unless the user picked one.
        if (!status.IsReady && Target == ExecutionTarget.Server)
            Target = ExecutionTarget.Local;
        else if (status.IsReady && !_targetChosenByUser && _remoteDefaults.DefaultTarget == ExecutionTarget.Server
                 && Target == ExecutionTarget.Local)
            Target = ExecutionTarget.Server;
        else
            SendValidationTarget();
        CreateJobFromSelectionCommand.NotifyCanExecuteChanged();
        CreateJobsForAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>For example "Busy: 4 jobs waiting, 2 of them from other clients, idle in about 5:00 h".</summary>
    internal static string DescribeLoad(QueueState load, DateTimeOffset now)
    {
        if (load.Running is null && load.WaitingJobs == 0)
            return "Server agent idle.";
        var foreign = load.WaitingJobs - load.OwnWaitingJobs;
        var text = load.WaitingJobs == 0
            ? "Busy: encoding one job"
            : $"Busy: {load.WaitingJobs} {(load.WaitingJobs == 1 ? "job" : "jobs")} waiting"
              + (foreign > 0 ? $", {foreign} of them from other clients" : "");
        if (load.EstimatedIdleAt is { } idle && idle > now)
            text += $", idle in about {RemoteProgressText.Duration((idle - now).TotalSeconds)}";
        return text + ".";
    }

    private static string DescribeUpdate(UpdateStatus update) => update.State switch
    {
        UpdateStateKind.WaitingForJobs => $"The agent installs {update.TargetVersion} after the running job.",
        UpdateStateKind.Downloading => $"The agent is downloading {update.TargetVersion}…",
        UpdateStateKind.Restarting => $"The agent is restarting with {update.TargetVersion}…",
        UpdateStateKind.Failed => $"Agent update failed: {update.Message}",
        UpdateStateKind.Idle => update.Message ?? "",
        _ => throw new ArgumentOutOfRangeException(nameof(update), update.State, "Unknown update state."),
    };

    private bool CanUpdateAgent => OffersAgentUpdate;

    [RelayCommand(CanExecute = nameof(CanUpdateAgent))]
    private async Task UpdateAgentAsync()
    {
        if (_agent is null)
            return;
        try
        {
            await _agent.RequestUpdateAsync(CancellationToken.None);
            UpdateServerState();
        }
        catch (RemoteAgentException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
        {
            await _dialogService.ShowErrorAsync("Agent cannot update itself",
                "This agent is too old to update from the app. Run this on the server:"
                + Environment.NewLine + Environment.NewLine + AgentInstallCommand.For(AppVersion.Current));
        }
        catch (RemoteAgentException ex)
        {
            await _dialogService.ShowErrorAsync("Agent update failed", ex.Message);
        }
    }

    /// <summary>The server job target for a new job, or null for a local job.</summary>
    private RemoteTarget? NewRemoteTarget(string label) =>
        Target == ExecutionTarget.Server && SelectedResultOption is { } option
            ? new RemoteTarget(Guid.NewGuid().ToString("N"), option.Mode, option.Root, label)
            : null;

    /// <summary>Validates against the FFmpeg that will run the job: the agent's for server jobs.</summary>
    private ValidationResult ValidateForTarget(OutputSettings settings, SourceMediaInfo media, double? duration = null) =>
        Target == ExecutionTarget.Server
            ? _validationService.Validate(settings, media, duration, _agent?.Status.Info?.Capabilities)
            : _validationService.Validate(settings, media, duration);

    /// <summary>
    /// Shows server jobs from the previous session again and continues them: unfinished uploads
    /// resume, finished encodes are downloaded. Jobs that failed earlier are removed from the agent.
    /// </summary>
    [RelayCommand]
    private async Task RestoreServerJobsAsync()
    {
        if (_remoteExecutor is null)
            return;
        var restored = 0;
        foreach (var record in _remoteExecutor.RestorableJobs())
        {
            if (Jobs.Any(job => job.QueuedJob.Remote?.Key == record.Key))
                continue;
            IReadOnlyList<GeneratedCommand> commands;
            try
            {
                commands = _commandBuilder.Build(record.Job, record.MediaInfo);
            }
            catch (CommandBuildException)
            {
                commands = [];
            }
            var queued = new QueuedJob(record.Job, record.MediaInfo, [.. commands], new ValidationResult(),
                new RemoteTarget(record.Key, record.ResultMode, record.ServerOutputRoot, record.DisplayLabel));
            AddJob(new JobItemViewModel($"job-{++_nextJobId}", record.MediaInfo, queued, record.DisplayLabel), queued);
            restored++;
        }
        UpdateSummary();
        if (restored > 0 && !IsRunning)
            StartCommand.Execute(null);

        try
        {
            await _remoteExecutor.CleanUpFinishedAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is RemoteAgentException or IOException)
        {
            StatusText = $"Could not clean up earlier server jobs: {ex.Message}";
        }
    }

    // ── Per-job control ───────────────────────────────────────────

    [RelayCommand]
    private void PauseJob(JobItemViewModel? job)
    {
        if (job is not null && job.State == JobState.Running && _queueService.PauseItem(job.Id))
            job.IsItemPaused = true;
    }

    [RelayCommand]
    private void ResumeJob(JobItemViewModel? job)
    {
        if (job is not null && _queueService.ResumeItem(job.Id))
            job.IsItemPaused = false;
    }

    [RelayCommand]
    private void CancelJob(JobItemViewModel? job)
    {
        if (job is null || !_queueService.CancelItem(job.Id))
            return;
        if (job.State == JobState.Pending)
            job.State = JobState.Canceled;
        UpdateSummary();
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
        (Target == ExecutionTarget.Local || IsServerAvailable)
        && !HasValidationErrors
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

        var validation = ValidateForTarget(
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

        var id = $"job-{++_nextJobId}";
        var label = Path.GetFileName(mediaInfo.SourcePath);
        var queuedJob = new QueuedJob(job, mediaInfo, [.. commands], validation, NewRemoteTarget(label));
        AddJob(new JobItemViewModel(id, mediaInfo, queuedJob, label), queuedJob);
    }

    private bool CanCreateJobsForAll =>
        (Target == ExecutionTarget.Local || IsServerAvailable)
        && !HasValidationErrors
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

            var validation = ValidateForTarget(settings,
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

            var queuedJob = new QueuedJob(job, mediaInfo, [.. commands], validation, NewRemoteTarget(source.DisplayLabel));
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
                            vm.UpdateProgress(progress, _timeProvider.GetUtcNow());
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
                            vm.IsItemPaused = false;
                            if (queueItem.Result is { } result)
                                vm.UpdateResult(result);
                            else if (queueItem.State == JobState.Failed)
                                vm.UpdateFailure(queueItem.FailureMessage);
                            NotifyServerJobFinished(vm);
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
    private async Task RemoveSelectedAsync()
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
        await ForgetServerJobsAsync(toRemove);
    }

    /// <summary>Removes the agent's copy of removed server jobs, so their sources are released.</summary>
    private async Task ForgetServerJobsAsync(IEnumerable<JobItemViewModel> removed)
    {
        if (_remoteExecutor is null)
            return;
        foreach (var job in removed)
        {
            if (job.QueuedJob.Remote is { } remote)
                await _remoteExecutor.ForgetAsync(remote.Key, CancellationToken.None);
        }
    }

    private void NotifyServerJobFinished(JobItemViewModel job)
    {
        if (_notifications is null || job.QueuedJob.Remote is not { } remote || _remoteExecutor?.IsDetached == true)
            return;
        var (title, message) = job.State switch
        {
            JobState.Completed when remote.ResultMode == ResultMode.KeepOnServer =>
                ("Server job finished", $"{job.DisplayLabel} is saved on the server."),
            JobState.Completed => ("Server job finished", $"{job.DisplayLabel} was downloaded."),
            JobState.Failed => ("Server job failed", job.Failure?.Message ?? job.DisplayLabel),
            JobState.Canceled => ("Server job canceled", job.DisplayLabel),
            _ => (null, null),
        };
        if (title is not null && message is not null)
            _notifications.Notify(title, message);
    }

    private bool CanClearFinished =>
        Jobs.Any(j => j.State is JobState.Completed or JobState.Failed or JobState.Canceled);

    [RelayCommand(CanExecute = nameof(CanClearFinished))]
    private async Task ClearFinishedAsync()
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
        await ForgetServerJobsAsync(finished);
    }

    private bool CanClearAll => Jobs.Count > 0 && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanClearAll))]
    private async Task ClearAllAsync()
    {
        var removed = Jobs.ToList();
        var ids = removed.Select(j => j.Id).ToList();
        if (ids.Count > 0)
            _queueService.Remove(ids);

        Jobs.Clear();
        SelectedJob = null;
        UpdateSummary();
        await ForgetServerJobsAsync(removed);
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
        var entries = Jobs.Where(job => !job.IsRemote).Select(job => new QueueEstimateEntry(
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
