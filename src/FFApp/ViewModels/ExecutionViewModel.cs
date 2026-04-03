using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Helpers;
using FFApp.Services;

namespace FFApp.ViewModels;

public partial class ExecutionViewModel : ObservableObject
{
    private readonly IJobExecutionService _jobExecutionService;
    private readonly IDialogService _dialogService;
    private readonly IValidationService? _validationService;
    private readonly StringBuilder _logBuilder = new();
    private CancellationTokenSource? _cts;
    private JobContext? _context;
    private bool _hasValidationErrors;

    [ObservableProperty]
    private string _stateText = "Ready";

    [ObservableProperty]
    private double _percentage;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _logText = "";

    [ObservableProperty]
    private string _outputPath = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _canStart;

    public string CommandText { get; private set; } = "";

    public ExecutionViewModel(IJobExecutionService jobExecutionService, IDialogService dialogService)
    {
        _jobExecutionService = jobExecutionService;
        _dialogService = dialogService;
    }

    public ExecutionViewModel(
        IJobExecutionService jobExecutionService,
        IDialogService dialogService,
        IValidationService validationService)
    {
        _jobExecutionService = jobExecutionService;
        _dialogService = dialogService;
        _validationService = validationService;
    }

    public void UpdateJobContext(JobContext? context)
    {
        _context = context;
        UpdateCanStart();
    }

    public void UpdateHasValidationErrors(bool hasErrors)
    {
        _hasValidationErrors = hasErrors;
        UpdateCanStart();
    }

    private bool CanStartJob() =>
        !IsRunning
        && _context is not null
        && !_hasValidationErrors;

    [RelayCommand(CanExecute = nameof(CanStartJob))]
    private async Task StartJobAsync()
    {
        if (_context is null)
            return;

        // Validate before starting (now with effective duration for correct bitrate)
        if (_validationService is not null)
        {
            var validation = _validationService.Validate(
                _context.Settings, _context.MediaInfo, _context.EffectiveDurationSeconds);
            if (validation.HasErrors)
            {
                await _dialogService.ShowErrorAsync(
                    "Validation Error",
                    string.Join(Environment.NewLine, validation.Errors.Select(e => e.Message)));
                return;
            }
        }

        var job = FormatHelpers.BuildJobDefinition(_context);
        var outputPath = FormatHelpers.ResolveOutputPath(_context);

        OutputPath = outputPath;
        IsRunning = true;
        StateText = "Running";
        Percentage = 0;
        ProgressText = "";
        _logBuilder.Clear();
        LogText = "";
        UpdateCanStart();
        StartJobCommand.NotifyCanExecuteChanged();
        CancelJobCommand.NotifyCanExecuteChanged();

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var result = await _jobExecutionService.ExecuteAsync(
                job,
                _context.MediaInfo,
                progress =>
                {
                    Dispatcher.UIThread.Post(() =>
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
                        ProgressText = string.Join(" | ", parts);
                        StateText = $"Pass {progress.PassIndex}/{progress.PassCount}";
                    });
                },
                line =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _logBuilder.AppendLine(line);
                        LogText = _logBuilder.ToString();
                    });
                },
                _cts.Token);

            StateText = result.State switch
            {
                JobState.Completed => "Completed",
                JobState.Failed => "Failed",
                JobState.Canceled => "Canceled",
                _ => result.State.ToString(),
            };

            if (result.State == JobState.Completed)
                Percentage = 100;
        }
        catch (OperationCanceledException)
        {
            StateText = "Canceled";
        }
        catch (Exception ex)
        {
            StateText = "Failed";
            _logBuilder.AppendLine($"Error: {ex.Message}");
            LogText = _logBuilder.ToString();
        }
        finally
        {
            IsRunning = false;
            UpdateCanStart();
            StartJobCommand.NotifyCanExecuteChanged();
            CancelJobCommand.NotifyCanExecuteChanged();
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void CancelJob()
    {
        _cts?.Cancel();
    }

    private void UpdateCanStart()
    {
        CanStart = CanStartJob();
        StartJobCommand.NotifyCanExecuteChanged();
    }
}
