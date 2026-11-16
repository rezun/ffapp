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

    private static readonly string[] EncoderNames =
        ["libx264", "libx265", "libvpx-vp9", "libsvtav1", "aac", "libmp3lame", "libvorbis", "libopus"];
    private static readonly string[] HdrFilterNames = ["zscale", "tonemap", "libplacebo"];

    [ObservableProperty] private ComponentStatus _ffmpegStatus = ComponentStatus.Empty;
    [ObservableProperty] private ComponentStatus _ffprobeStatus = ComponentStatus.Empty;
    [ObservableProperty] private ComponentStatus _mkvpropeditStatus = ComponentStatus.Empty;
    [ObservableProperty] private ComponentStatus _encodersStatus = ComponentStatus.Empty;
    [ObservableProperty] private ComponentStatus _hdrFiltersStatus = ComponentStatus.Empty;
    [ObservableProperty] private ComponentStatus _comparisonStatus = ComponentStatus.Empty;
    public ComponentStatus PreviewStatus => _isVlcAvailable
        ? new("Main-window preview", "Available through VLC.", ComponentState.Ok)
        : new("Main-window preview", "Needs VLC for the app architecture. Restart FFApp after installing it.", ComponentState.Warning);

    [ObservableProperty]
    private string _ffmpegPath = "";

    [ObservableProperty]
    private string _ffprobePath = "";

    [ObservableProperty]
    private string _mkvpropeditPath = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private ComponentState _statusState = ComponentState.Info;

    public ComponentStatus Summary => new("", StatusText, StatusState);

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

    public void ShowError(string message) => SetStatus(message, ComponentState.Error);

    private void SetStatus(string message, ComponentState state)
    {
        StatusText = message;
        StatusState = state;
    }

    private async Task ApplyStatusAsync(DependencyStatus status)
    {
        FfmpegPath = status.Ffmpeg.Path ?? FfmpegPath;
        FfprobePath = status.Ffprobe.Path ?? FfprobePath;
        MkvpropeditPath = status.Mkvpropedit.Path ?? MkvpropeditPath;
        FfmpegStatus = DescribeTool(status.Ffmpeg, ComponentState.Error);
        FfprobeStatus = DescribeTool(status.Ffprobe, ComponentState.Error);
        MkvpropeditStatus = status.Mkvpropedit.IsValid || status.Mkvpropedit.Path is not null
            ? DescribeTool(status.Mkvpropedit, ComponentState.Warning)
            : new("", "Not found. Matroska track statistics are not refreshed after encoding.", ComponentState.Warning);
        EncodersStatus = DescribeFeatures("Encoders", EncoderNames, status.Capabilities?.Encoders);
        HdrFiltersStatus = DescribeFeatures("HDR filters", HdrFilterNames, status.Capabilities?.Filters);

        var runtime = await Task.Run(() => _comparisonRuntime.Check(status.Ffmpeg.Path ?? "ffmpeg"));
        ComparisonStatus = new("Comparison playback", runtime.Message,
            runtime is { IsAvailable: true, IsHdrToneMappingAvailable: true } ? ComponentState.Ok : ComponentState.Warning);

        if (!status.IsReady)
            SetStatus(status.RequiredToolsError, ComponentState.Error);
        else if (new[] { MkvpropeditStatus, EncodersStatus, HdrFiltersStatus, ComparisonStatus, PreviewStatus }.All(item => item.IsOk))
            SetStatus("Everything is set up.", ComponentState.Ok);
        else
            SetStatus("Ready to encode. Some optional features are limited; see the details above.", ComponentState.Ok);
    }

    private static ComponentStatus DescribeTool(ExecutableValidation tool, ComponentState failure) => tool.IsValid
        ? new("", ShortVersion(tool.VersionOutput) ?? "Found.", ComponentState.Ok)
        : new("", tool.Error ?? $"{tool.Name} is not available.", failure);

    /// <summary>Drops the copyright notice that FFmpeg appends to its version line.</summary>
    private static string? ShortVersion(string? versionLine)
    {
        if (versionLine is null) return null;
        var copyright = versionLine.IndexOf(" Copyright", StringComparison.Ordinal);
        return copyright < 0 ? versionLine : versionLine[..copyright];
    }

    private static ComponentStatus DescribeFeatures(string title, IReadOnlyList<string> names, IReadOnlySet<string>? available)
    {
        if (available is null)
            return new(title, "Not checked because FFmpeg is unavailable.", ComponentState.Info);
        var missing = names.Where(name => !available.Contains(name)).ToList();
        return missing.Count == 0
            ? new(title, string.Join(", ", names), ComponentState.Ok)
            : new(title, $"Missing {string.Join(", ", missing)}. Settings that need them are blocked before encoding.", ComponentState.Warning);
    }

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
            SetStatus("Checking tools...", ComponentState.Info);

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

            SetStatus("Saved. " + StatusText, ComponentState.Ok);
            _messenger.Send(new DependenciesResolvedMessage(status));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking or saving media tools failed");
            ShowError($"Checking the tools failed: {ex.Message}");
        }
        finally
        {
            IsValidating = false;
            ValidateAndSaveCommand.NotifyCanExecuteChanged();
            CheckAgainCommand.NotifyCanExecuteChanged();
        }
    }
}
