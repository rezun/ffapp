using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;

namespace FFApp.ViewModels;

public partial class JobsPanelViewModel : ObservableObject
{
    public ExecutionViewModel Execution { get; }
    public QueuePanelViewModel QueuePanel { get; }

    [ObservableProperty]
    private bool _isQueueMode;

    public bool IsSingleMode => !IsQueueMode;

    [ObservableProperty]
    private bool _isAnyJobRunning;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private double _statusPercentage;

    public JobsPanelViewModel(ExecutionViewModel execution, QueuePanelViewModel queuePanel)
    {
        Execution = execution;
        QueuePanel = queuePanel;

        Execution.PropertyChanged += OnExecutionPropertyChanged;
        QueuePanel.PropertyChanged += OnQueuePropertyChanged;
        QueuePanel.Items.CollectionChanged += (_, _) => UpdateRunningItemSubscription();
    }

    partial void OnIsQueueModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSingleMode));
    }

    [RelayCommand]
    private void SetSingleMode() => IsQueueMode = false;

    [RelayCommand]
    private void SetQueueMode() => IsQueueMode = true;

    public void UpdateJobContext(JobContext? context)
    {
        Execution.UpdateJobContext(context);
    }

    private QueueItemViewModel? _trackedRunningItem;

    private void OnExecutionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsQueueMode) return;

        if (e.PropertyName is nameof(ExecutionViewModel.IsRunning))
        {
            UpdateStatusFromExecution();
        }
        else if (e.PropertyName is nameof(ExecutionViewModel.ProgressText)
                 or nameof(ExecutionViewModel.Percentage)
                 or nameof(ExecutionViewModel.OutputPath)
                 or nameof(ExecutionViewModel.StateText))
        {
            if (Execution.IsRunning)
                UpdateStatusFromExecution();
        }
    }

    private void OnQueuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsQueueMode) return;

        if (e.PropertyName is nameof(QueuePanelViewModel.IsRunning))
        {
            UpdateStatusFromQueue();
            UpdateRunningItemSubscription();
        }
        else if (e.PropertyName is nameof(QueuePanelViewModel.SummaryText))
        {
            if (QueuePanel.IsRunning)
                UpdateStatusFromQueue();
        }
    }

    private void UpdateRunningItemSubscription()
    {
        // Unsubscribe from previous
        if (_trackedRunningItem is not null)
        {
            _trackedRunningItem.PropertyChanged -= OnRunningItemPropertyChanged;
            _trackedRunningItem = null;
        }

        if (!QueuePanel.IsRunning) return;

        // Find the currently running item
        _trackedRunningItem = QueuePanel.Items.FirstOrDefault(i => i.State == JobState.Running);
        if (_trackedRunningItem is not null)
        {
            _trackedRunningItem.PropertyChanged += OnRunningItemPropertyChanged;
            UpdateStatusFromQueue();
        }
    }

    private void OnRunningItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueueItemViewModel.Percentage)
            or nameof(QueueItemViewModel.ProgressText)
            or nameof(QueueItemViewModel.State))
        {
            // If item is no longer running, find the next one
            if (_trackedRunningItem?.State != JobState.Running)
            {
                UpdateRunningItemSubscription();
                return;
            }

            UpdateStatusFromQueue();
        }
    }

    private void UpdateStatusFromExecution()
    {
        IsAnyJobRunning = Execution.IsRunning;

        if (!Execution.IsRunning)
        {
            StatusText = "";
            StatusPercentage = 0;
            return;
        }

        var filename = Path.GetFileName(Execution.OutputPath);
        var progress = Execution.ProgressText;
        StatusText = string.IsNullOrEmpty(progress)
            ? $"Processing: {filename}"
            : $"Processing: {filename}  |  {progress}";
        StatusPercentage = Execution.Percentage;
    }

    private void UpdateStatusFromQueue()
    {
        IsAnyJobRunning = QueuePanel.IsRunning;

        if (!QueuePanel.IsRunning)
        {
            StatusText = "";
            StatusPercentage = 0;
            return;
        }

        var runningItem = _trackedRunningItem;
        if (runningItem is not null)
        {
            var filename = Path.GetFileName(runningItem.SourcePath);
            var progress = runningItem.ProgressText;
            StatusText = string.IsNullOrEmpty(progress)
                ? $"Queue: {filename}"
                : $"Queue: {filename}  |  {progress}";
            StatusPercentage = runningItem.Percentage;
        }
        else
        {
            StatusText = $"Queue: {QueuePanel.SummaryText}";
            StatusPercentage = 0;
        }
    }
}
