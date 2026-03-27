using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core;
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
    private OutputSettings? _currentSettings;
    private SourceMediaInfo? _currentMediaInfo;
    private string _ffmpegPath = "ffmpeg";
    private double? _trimStartSeconds;
    private double? _trimEndSeconds;
    private CropRect? _crop;
    private string? _outputDirectory;
    private string? _outputFilename;
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

    public void SetFfmpegPath(string path)
    {
        _ffmpegPath = path;
    }

    public void UpdateSourceInfo(SourceMediaInfo mediaInfo)
    {
        _currentMediaInfo = mediaInfo;
        UpdateCanStart();
    }

    public void UpdateOutputSettings(OutputSettings settings)
    {
        _currentSettings = settings;
        UpdateCanStart();
    }

    public void UpdateContext(OutputSettings settings, SourceMediaInfo? mediaInfo)
    {
        _currentSettings = settings;
        _currentMediaInfo = mediaInfo;
        UpdateCanStart();
    }

    public void UpdateTrim(double? trimStartSeconds, double? trimEndSeconds)
    {
        _trimStartSeconds = trimStartSeconds;
        _trimEndSeconds = trimEndSeconds;
    }

    public void UpdateCrop(CropRect? crop)
    {
        _crop = crop;
    }

    public void UpdateOutputPath(string? directory, string? filename)
    {
        _outputDirectory = directory;
        _outputFilename = filename;
    }

    public void UpdateHasValidationErrors(bool hasErrors)
    {
        _hasValidationErrors = hasErrors;
        UpdateCanStart();
    }

    private bool CanStartJob() =>
        !IsRunning
        && _currentMediaInfo is not null
        && _currentSettings is not null
        && !_hasValidationErrors;

    [RelayCommand(CanExecute = nameof(CanStartJob))]
    private async Task StartJobAsync()
    {
        if (_currentSettings is null || _currentMediaInfo is null)
            return;

        // Validate before starting
        if (_validationService is not null)
        {
            var validation = _validationService.Validate(_currentSettings, _currentMediaInfo);
            if (validation.HasErrors)
            {
                await _dialogService.ShowErrorAsync(
                    "Validation Error",
                    string.Join(Environment.NewLine, validation.Errors.Select(e => e.Message)));
                return;
            }
        }

        var outputPath = DefaultCommandBuilder.SuggestOutputPath(
            _currentMediaInfo.SourcePath, _currentSettings.Container,
            _outputDirectory, _outputFilename);

        var job = FormatHelpers.BuildJobDefinition(
            _currentMediaInfo.SourcePath, outputPath, _currentSettings,
            _trimStartSeconds, _trimEndSeconds, _crop);

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
                _currentMediaInfo,
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
