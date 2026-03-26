using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Helpers;
using FFApp.Services;

namespace FFApp.ViewModels;

public partial class SourcePanelViewModel : ObservableObject
{
    private readonly IMediaProbeService _mediaProbeService;
    private readonly IDialogService _dialogService;

    [ObservableProperty]
    private string? _sourcePath;

    [ObservableProperty]
    private SourceMediaInfo? _mediaInfo;

    [ObservableProperty]
    private bool _isProbing;

    [ObservableProperty]
    private string _statusText = "No source file loaded.";

    public string MediaSummaryText =>
        MediaInfo is null ? "" : FormatHelpers.BuildMediaSummaryText(MediaInfo);

    public SourcePanelViewModel(IMediaProbeService mediaProbeService, IDialogService dialogService)
    {
        _mediaProbeService = mediaProbeService;
        _dialogService = dialogService;
    }

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        var path = await _dialogService.OpenFileAsync("Select source media file");
        if (path is not null)
            await LoadSource(path);
    }

    [RelayCommand]
    private async Task PastePathAsync(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            await LoadSource(path.Trim());
    }

    public async Task LoadSource(string path)
    {
        if (!File.Exists(path))
        {
            StatusText = "File not found.";
            return;
        }

        SourcePath = path;
        IsProbing = true;
        StatusText = "Probing file...";

        try
        {
            var info = await _mediaProbeService.ProbeAsync(path);
            MediaInfo = info;
            StatusText = $"Loaded: {Path.GetFileName(path)}";
            OnPropertyChanged(nameof(MediaSummaryText));
            WeakReferenceMessenger.Default.Send(new SourceLoadedMessage(info));
        }
        catch (Exception ex)
        {
            MediaInfo = null;
            StatusText = $"Probe failed: {ex.Message}";
            OnPropertyChanged(nameof(MediaSummaryText));
        }
        finally
        {
            IsProbing = false;
        }
    }
}
