using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FFRux.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFRux.Views;

public sealed partial class ToolsWindow : Window
{
    public ILogger Logger { get; set; } = NullLogger<ToolsWindow>.Instance;
    public ToolsWindow() => InitializeComponent();

    // async void: Avalonia click events are synchronous. Surface picker/launcher failures in the dialog.
    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tool } || DataContext is not MainWindowViewModel vm) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = $"Select {tool} executable" });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            switch (tool)
            {
                case "ffmpeg": vm.FfmpegPath = path; break;
                case "ffprobe": vm.FfprobePath = path; break;
                case "mkvpropedit": vm.MkvpropeditPath = path; break;
                default: throw new InvalidOperationException($"Unknown tool: {tool}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Selecting a tool executable failed");
            vm.ToolDetails = $"Could not select executable: {ex.Message}";
        }
    }

    // async void: Avalonia click events are synchronous. Surface launcher failures in the dialog.
    private async void OnDownload(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || DataContext is not MainWindowViewModel vm) return;
        try { await Launcher.LaunchUriAsync(new Uri(url)); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Opening a tool download page failed");
            vm.ToolDetails = $"Could not open download page: {ex.Message}";
        }
    }
}
