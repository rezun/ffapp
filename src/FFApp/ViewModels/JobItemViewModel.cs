using System.Text;
using Avalonia.Data.Converters;
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
    public QueuedJob QueuedJob { get; }

    [ObservableProperty]
    private string _displayLabel;

    [ObservableProperty]
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

    public JobItemViewModel(string id, SourceMediaInfo mediaInfo, QueuedJob queuedJob, string displayLabel)
    {
        Id = id;
        SourcePath = mediaInfo.SourcePath;
        MediaInfo = mediaInfo;
        QueuedJob = queuedJob;
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
    public static readonly FuncValueConverter<bool, string> ExpandCollapseConverter =
        new(expanded => expanded ? "\u25BC Collapse" : "\u25B6 Expand");

    private JobItemViewModel? _trackedItem;

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private string _commandText = "";

    [ObservableProperty]
    private string _logText = "";

    [ObservableProperty]
    private bool _isLogExpanded;

    [ObservableProperty]
    private bool _isCommandExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    private JobFailure? _failure;

    public bool HasFailure => Failure is not null;

    [RelayCommand]
    private void ToggleLogExpanded() => IsLogExpanded = !IsLogExpanded;

    public void UpdateFromItem(JobItemViewModel? item)
    {
        if (_trackedItem is not null)
            _trackedItem.PropertyChanged -= OnTrackedItemPropertyChanged;

        _trackedItem = item;
        IsCommandExpanded = item?.HasFailure != true;

        if (_trackedItem is not null)
            _trackedItem.PropertyChanged += OnTrackedItemPropertyChanged;

        Refresh();
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
    }

    private void Refresh()
    {
        var item = _trackedItem;
        if (item is null)
        {
            SummaryText = "";
            CommandText = "";
            LogText = "";
            Failure = null;
            return;
        }

        var job = item.QueuedJob.Job;
        if (!HasFailure && item.HasFailure)
            IsCommandExpanded = false;
        Failure = item.Failure;
        SummaryText = $"{Path.GetFileName(job.SourcePath)} -> {Path.GetFileName(job.OutputPath)} ({item.State})";
        CommandText = string.Join(
            Environment.NewLine + Environment.NewLine,
            item.QueuedJob.Commands.Select(c => c.DisplayString));

        LogText = string.IsNullOrEmpty(item.LogText) ? item.Result?.LogOutput ?? "" : item.LogText;
    }
}
