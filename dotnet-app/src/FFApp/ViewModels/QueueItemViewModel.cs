using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;

namespace FFApp.ViewModels;

public partial class QueueItemViewModel : ObservableObject
{
    public string Id { get; }
    public string SourcePath { get; }
    public SourceMediaInfo MediaInfo { get; }

    /// <summary>
    /// Set when the queue starts — built from current settings at that time.
    /// </summary>
    public QueuedJob? QueuedJob { get; private set; }

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

    public QueueItemViewModel(string id, SourceMediaInfo mediaInfo, string displayLabel)
    {
        Id = id;
        SourcePath = mediaInfo.SourcePath;
        MediaInfo = mediaInfo;
        _displayLabel = displayLabel;
    }

    public void ResolveWithSettings(QueuedJob queuedJob)
    {
        QueuedJob = queuedJob;
    }

    public void ResetToPending()
    {
        QueuedJob = null;
        State = JobState.Pending;
        Percentage = 0;
        ProgressText = "";
        LogText = "";
        Result = null;
    }

    public void UpdateProgress(JobProgress progress)
    {
        Percentage = progress.Percentage ?? 0;

        var parts = new List<string>();
        if (progress.Percentage is { } pct)
            parts.Add($"{pct:F1}%");
        if (progress.Speed is { } speed)
            parts.Add($"{speed:F1}x");
        parts.Add($"Pass {progress.PassIndex}/{progress.PassCount}");
        ProgressText = string.Join(" | ", parts);
    }

    public void UpdateResult(JobResult result)
    {
        State = result.State;
        Result = result;

        if (result.State == JobState.Completed)
            Percentage = 100;
    }

    public void AppendLog(string line)
    {
        LogText += line + Environment.NewLine;
    }
}

public partial class QueueItemDetailsViewModel : ObservableObject
{
    private QueueItemViewModel? _trackedItem;

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private string _commandText = "";

    [ObservableProperty]
    private string _logText = "";

    public void UpdateFromItem(QueueItemViewModel? item)
    {
        if (_trackedItem is not null)
            _trackedItem.PropertyChanged -= OnTrackedItemPropertyChanged;

        _trackedItem = item;

        if (_trackedItem is not null)
            _trackedItem.PropertyChanged += OnTrackedItemPropertyChanged;

        Refresh();
    }

    private void OnTrackedItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueueItemViewModel.LogText)
            or nameof(QueueItemViewModel.State)
            or nameof(QueueItemViewModel.ProgressText))
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
            return;
        }

        if (item.QueuedJob is { } queuedJob)
        {
            var job = queuedJob.Job;
            SummaryText = $"{Path.GetFileName(job.SourcePath)} -> {Path.GetFileName(job.OutputPath)} ({item.State})";
            CommandText = string.Join(
                Environment.NewLine + Environment.NewLine,
                queuedJob.Commands.Select(c => c.DisplayString));
        }
        else
        {
            SummaryText = $"{Path.GetFileName(item.SourcePath)} ({item.State})";
            CommandText = "Commands will be generated from current settings when the queue starts.";
        }

        LogText = item.LogText;
    }
}
