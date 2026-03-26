using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;

namespace FFApp.ViewModels;

public partial class DependencySetupViewModel : ObservableObject
{
    private readonly IDependencyService _dependencyService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;

    [ObservableProperty]
    private string _ffmpegPath = "";

    [ObservableProperty]
    private string _ffprobePath = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isValidating;

    public DependencySetupViewModel(
        IDependencyService dependencyService,
        ISettingsService settingsService,
        IDialogService dialogService)
    {
        _dependencyService = dependencyService;
        _settingsService = settingsService;
        _dialogService = dialogService;
    }

    public void SetFromSettings(AppSettings settings)
    {
        FfmpegPath = settings.ExecutablePaths.Ffmpeg ?? "";
        FfprobePath = settings.ExecutablePaths.Ffprobe ?? "";
    }

    [RelayCommand]
    private async Task BrowseFfmpegAsync()
    {
        var path = await _dialogService.OpenFileAsync("Select ffmpeg executable");
        if (path is not null)
            FfmpegPath = path;
    }

    [RelayCommand]
    private async Task BrowseFfprobeAsync()
    {
        var path = await _dialogService.OpenFileAsync("Select ffprobe executable");
        if (path is not null)
            FfprobePath = path;
    }

    private bool CanValidate() => !IsValidating;

    [RelayCommand(CanExecute = nameof(CanValidate))]
    private async Task ValidateAndSaveAsync()
    {
        IsValidating = true;
        ValidateAndSaveCommand.NotifyCanExecuteChanged();

        try
        {
            StatusText = "Validating paths...";

            var paths = new ExecutablePaths(FfmpegPath, FfprobePath);
            var status = await _dependencyService.ValidatePathsAsync(paths);

            if (!status.IsReady)
            {
                var errors = new List<string>();
                if (!status.Ffmpeg.IsValid)
                    errors.Add($"ffmpeg: {status.Ffmpeg.Error ?? "invalid"}");
                if (!status.Ffprobe.IsValid)
                    errors.Add($"ffprobe: {status.Ffprobe.Error ?? "invalid"}");

                StatusText = string.Join("; ", errors);
                return;
            }

            var settings = await _settingsService.LoadAsync();
            settings = settings with { ExecutablePaths = paths };
            await _settingsService.SaveAsync(settings);

            StatusText = "Dependencies validated successfully.";
            WeakReferenceMessenger.Default.Send(new DependenciesResolvedMessage(status));
        }
        catch (Exception ex)
        {
            StatusText = $"Validation failed: {ex.Message}";
        }
        finally
        {
            IsValidating = false;
            ValidateAndSaveCommand.NotifyCanExecuteChanged();
        }
    }
}
