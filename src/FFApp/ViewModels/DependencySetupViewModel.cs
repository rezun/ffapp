using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFApp.ViewModels;

public partial class DependencySetupViewModel : ObservableObject
{
    private readonly IDependencyService _dependencyService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private readonly IMessenger _messenger;
    private readonly IComparisonRuntimeService _comparisonRuntime;
    private readonly bool _isVlcAvailable;
    private readonly ILogger _logger;

    [ObservableProperty] private string _ffmpegStatus = "";
    [ObservableProperty] private string _ffprobeStatus = "";
    [ObservableProperty] private string _mkvpropeditStatus = "";
    [ObservableProperty] private string _encodingStatus = "";
    [ObservableProperty] private string _comparisonStatus = "";
    public string ComponentNotice { get; private set; } = "";
    public string VlcStatus => _isVlcAvailable ? "Main-window preview: VLC available."
        : "Main-window preview: VLC unavailable. Install VLC matching the app architecture and restart FFApp. Encoding remains available.";

    [ObservableProperty]
    private string _ffmpegPath = "";

    [ObservableProperty]
    private string _ffprobePath = "";

    [ObservableProperty]
    private string _mkvpropeditPath = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isValidating;

    public DependencySetupViewModel(
        IDependencyService dependencyService,
        ISettingsService settingsService,
        IDialogService dialogService,
        IMessenger messenger,
        IComparisonRuntimeService comparisonRuntime,
        LibVLC? libVlc = null,
        ILogger<DependencySetupViewModel>? logger = null)
    {
        _dependencyService = dependencyService;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _messenger = messenger;
        _comparisonRuntime = comparisonRuntime;
        _isVlcAvailable = libVlc is not null;
        _logger = logger ?? NullLogger<DependencySetupViewModel>.Instance;
    }

    public async Task InitializeAsync(AppSettings settings, DependencyStatus status)
    {
        SetFromSettings(settings);
        await ApplyStatusAsync(status);
    }

    private async Task ApplyStatusAsync(DependencyStatus status)
    {
        FfmpegPath = status.Ffmpeg.Path ?? FfmpegPath;
        FfprobePath = status.Ffprobe.Path ?? FfprobePath;
        MkvpropeditPath = status.Mkvpropedit.Path ?? MkvpropeditPath;
        FfmpegStatus = Describe(status.Ffmpeg);
        FfprobeStatus = Describe(status.Ffprobe);
        MkvpropeditStatus = Describe(status.Mkvpropedit);
        StatusText = status.IsReady ? "Required tools are ready. Optional components do not block encoding." : status.RequiredToolsError;
        var runtime = await Task.Run(() => _comparisonRuntime.Check(status.Ffmpeg.Path ?? "ffmpeg"));
        ComparisonStatus = runtime.Message;
        var unavailable = new List<string>();
        if (!runtime.IsAvailable) unavailable.Add("comparison playback");
        else if (!runtime.IsHdrToneMappingAvailable) unavailable.Add("HDR comparison tone mapping");
        if (!_isVlcAvailable) unavailable.Add("VLC preview");
        if (!status.Mkvpropedit.IsValid) unavailable.Add("Matroska statistics");
        if (status.Capabilities is { } detected
            && new[] { "libx264", "libx265", "libvpx-vp9", "libsvtav1", "aac", "libmp3lame", "libvorbis", "libopus" }
                .Any(name => !detected.Encoders.Contains(name))) unavailable.Add("some encoders");
        ComponentNotice = unavailable.Count == 0 ? "" : "Optional features unavailable: " + string.Join(", ", unavailable) + ".";
        EncodingStatus = status.Capabilities is not { } capabilities ? "Encoding features have not been checked."
            : "Encoders: " + string.Join(", ", new[] { "libx264", "libx265", "libvpx-vp9", "libsvtav1", "aac", "libmp3lame", "libvorbis", "libopus" }
                .Select(name => $"{name}: {(capabilities.Encoders.Contains(name) ? "available" : "missing")}"))
                + Environment.NewLine + "HDR filters: " + string.Join(", ", new[] { "zscale", "tonemap", "libplacebo" }
                .Select(name => $"{name}: {(capabilities.Filters.Contains(name) ? "available" : "missing")}"))
                + ". Settings requiring missing features are blocked before encoding.";
    }

    private static string Describe(ExecutableValidation tool) => tool.IsValid
        ? tool.VersionOutput ?? $"{tool.Name} available." : tool.Error ?? $"{tool.Name} unavailable.";

    public void SetFromSettings(AppSettings settings)
    {
        FfmpegPath = settings.ExecutablePaths.Ffmpeg ?? "";
        FfprobePath = settings.ExecutablePaths.Ffprobe ?? "";
        MkvpropeditPath = settings.ExecutablePaths.Mkvpropedit ?? "";
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

    [RelayCommand]
    private async Task BrowseMkvpropeditAsync()
    {
        var path = await _dialogService.OpenFileAsync("Select mkvpropedit executable");
        if (path is not null)
            MkvpropeditPath = path;
    }

    private bool CanValidate() => !IsValidating;

    [RelayCommand(CanExecute = nameof(CanValidate))]
    private async Task CheckAgainAsync() => await CheckAsync(save: false);

    [RelayCommand(CanExecute = nameof(CanValidate))]
    private async Task ValidateAndSaveAsync()
        => await CheckAsync(save: true);

    private async Task CheckAsync(bool save)
    {
        IsValidating = true;
        ValidateAndSaveCommand.NotifyCanExecuteChanged();
        CheckAgainCommand.NotifyCanExecuteChanged();

        try
        {
            StatusText = "Validating paths...";

            var paths = new ExecutablePaths(
                FfmpegPath,
                FfprobePath,
                string.IsNullOrWhiteSpace(MkvpropeditPath) ? null : MkvpropeditPath);
            var status = await _dependencyService.ValidatePathsAsync(paths);
            await ApplyStatusAsync(status);
            if (!status.IsReady || !save) return;

            var settings = await _settingsService.LoadAsync();
            settings = settings with { ExecutablePaths = status.ExecutablePaths };
            await _settingsService.SaveAsync(settings);

            StatusText = "Dependencies validated successfully.";
            _messenger.Send(new DependenciesResolvedMessage(status, ComponentNotice));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking or saving media tools failed");
            StatusText = $"Validation failed: {ex.Message}";
        }
        finally
        {
            IsValidating = false;
            ValidateAndSaveCommand.NotifyCanExecuteChanged();
            CheckAgainCommand.NotifyCanExecuteChanged();
        }
    }
}
