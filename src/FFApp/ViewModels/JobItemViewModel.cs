using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;
using FFApp.Helpers;

namespace FFApp.ViewModels;

public sealed partial class JobItemViewModel : ObservableObject
{
    private readonly StringBuilder _logBuilder = new();

    public string Id { get; }
    public string SourcePath { get; }
    public SourceMediaInfo MediaInfo { get; }

    [ObservableProperty]
    private QueuedJob _queuedJob;

    [ObservableProperty]
    private string _displayLabel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRenameOutput))]
    private JobState _state = JobState.Pending;

    [ObservableProperty]
    private double _percentage;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _logText = "";

    [ObservableProperty]
    private JobResult? _result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    private JobFailure? _failure;

    public bool HasFailure => Failure is not null;

    /// <summary>Jobs that have not started, or will re-run, can still change their output name.</summary>
    public bool CanRenameOutput => State is JobState.Pending or JobState.Failed or JobState.Canceled;

    public JobItemViewModel(string id, SourceMediaInfo mediaInfo, QueuedJob queuedJob, string displayLabel)
    {
        Id = id;
        SourcePath = mediaInfo.SourcePath;
        MediaInfo = mediaInfo;
        _queuedJob = queuedJob;
        _displayLabel = displayLabel;
    }

    public void ResetToPending()
    {
        State = JobState.Pending;
        Percentage = 0;
        ProgressText = "";
        _logBuilder.Clear();
        LogText = "";
        Result = null;
        Failure = null;
    }

    public void UpdateProgress(JobProgress progress)
    {
        Percentage = progress.Percentage ?? 0;

        var parts = new List<string>();
        if (progress.Percentage is { } pct)
            parts.Add($"{pct:F1}%");
        if (progress.Speed is { } speed)
            parts.Add($"{speed:F1}x");
        parts.Add(FormatHelpers.FormatCompactDuration(progress.ElapsedSeconds));
        var remaining = FormatHelpers.EstimateRemainingSeconds(
            progress.ElapsedSeconds, progress.Percentage);
        if (remaining is not null)
            parts.Add($"~{FormatHelpers.FormatCompactDuration(remaining.Value)} left");
        if (progress.PassCount > 1)
            parts.Add($"Pass {progress.PassIndex}/{progress.PassCount}");
        ProgressText = string.Join(" | ", parts);
    }

    public void UpdateResult(JobResult result)
    {
        State = result.State;
        Result = result;
        Failure = result.State == JobState.Failed ? JobFailureAnalyzer.Analyze(result) : null;

        if (result.State == JobState.Completed)
            Percentage = 100;
    }

    public void UpdateFailure(string? message)
    {
        State = JobState.Failed;
        Failure = JobFailureAnalyzer.Analyze(null, message);
    }

    public void AppendLog(string line)
    {
        _logBuilder.AppendLine(line);
        LogText = _logBuilder.ToString();
    }
}

public sealed partial class JobItemDetailsViewModel : ObservableObject
{
    private readonly Func<JobItemViewModel, string, Task>? _renameOutput;
    private JobItemViewModel? _trackedItem;

    /// <param name="renameOutput">Applies a new output file name (without extension) to a job.</param>
    public JobItemDetailsViewModel(Func<JobItemViewModel, string, Task>? renameOutput = null)
    {
        _renameOutput = renameOutput;
    }

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private string _sourcePath = "";

    [ObservableProperty]
    private string _outputPath = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameOutputCommand))]
    private string _outputFileStem = "";

    [ObservableProperty]
    private string _outputExtension = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameOutputCommand))]
    private bool _canRenameOutput;

    [ObservableProperty]
    private string _stateText = "";

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _commandText = "";

    [ObservableProperty]
    private string _logText = "";

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    private JobFailure? _failure;

    public bool HasFailure => Failure is not null;

    public void UpdateFromItem(JobItemViewModel? item)
    {
        if (_trackedItem is not null)
            _trackedItem.PropertyChanged -= OnTrackedItemPropertyChanged;

        _trackedItem = item;
        if (item is null || item.HasFailure)
            SelectedTabIndex = 0;

        if (_trackedItem is not null)
            _trackedItem.PropertyChanged += OnTrackedItemPropertyChanged;

        Refresh();
        ResetOutputFileStem();
    }

    private bool CanExecuteRenameOutput =>
        _renameOutput is not null
        && CanRenameOutput
        && !string.IsNullOrWhiteSpace(OutputFileStem)
        && !string.Equals(OutputFileStem, CurrentOutputStem(), StringComparison.Ordinal);

    [RelayCommand(CanExecute = nameof(CanExecuteRenameOutput))]
    private async Task RenameOutputAsync()
    {
        if (_renameOutput is null || _trackedItem is not { } item)
            return;

        await _renameOutput(item, OutputFileStem);

        // Show the applied name, or restore the current one when the rename was refused
        if (ReferenceEquals(_trackedItem, item))
            ResetOutputFileStem();
    }

    private string CurrentOutputStem() =>
        _trackedItem is null ? "" : Path.GetFileNameWithoutExtension(_trackedItem.QueuedJob.Job.OutputPath);

    /// <summary>
    /// Loads the editable name from the job. Not part of <see cref="Refresh"/>, which runs on every
    /// log line and would discard what the user is typing.
    /// </summary>
    private void ResetOutputFileStem()
    {
        OutputFileStem = CurrentOutputStem();
        OutputExtension = _trackedItem is null ? "" : Path.GetExtension(_trackedItem.QueuedJob.Job.OutputPath);
        RenameOutputCommand.NotifyCanExecuteChanged();
    }

    private void OnTrackedItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(JobItemViewModel.LogText)
            or nameof(JobItemViewModel.State)
            or nameof(JobItemViewModel.Failure)
            or nameof(JobItemViewModel.Result)
            or nameof(JobItemViewModel.ProgressText))
        {
            Refresh();
        }

        if (e.PropertyName is nameof(JobItemViewModel.QueuedJob))
        {
            Refresh();
            ResetOutputFileStem();
        }
    }

    private void Refresh()
    {
        var item = _trackedItem;
        if (item is null)
        {
            HasSelection = false;
            SourcePath = "";
            OutputPath = "";
            CanRenameOutput = false;
            StateText = "";
            ProgressText = "";
            CommandText = "";
            LogText = "";
            Failure = null;
            return;
        }

        var job = item.QueuedJob.Job;
        if (!HasFailure && item.HasFailure)
            SelectedTabIndex = 0;
        Failure = item.Failure;
        HasSelection = true;
        SourcePath = job.SourcePath;
        OutputPath = job.OutputPath;
        CanRenameOutput = item.CanRenameOutput;
        StateText = item.State.ToString();
        ProgressText = item.ProgressText;
        CommandText = string.Join(
            Environment.NewLine + Environment.NewLine,
            item.QueuedJob.Commands.Select(c => c.DisplayString));

        LogText = string.IsNullOrEmpty(item.LogText) ? item.Result?.LogOutput ?? "" : item.LogText;
    }
}
