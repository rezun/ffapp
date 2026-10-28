using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FFRux.Core.Models;

namespace FFRux.Services;

public sealed class DialogService(Func<Window> windowAccessor) : IDialogService
{
    private static readonly FilePickerFileType SourceFiles = new("Media and subtitle files")
    {
        Patterns =
        [
            "*.mkv", "*.mka", "*.mk3d", "*.mp4", "*.m4v", "*.m4a", "*.m4b", "*.mov", "*.webm", "*.ts", "*.m2ts", "*.mts",
            "*.ogg", "*.ogv", "*.opus", "*.avi", "*.aac", "*.ac3", "*.eac3", "*.ec3", "*.dts", "*.thd", "*.mlp", "*.flac",
            "*.mp3", "*.wav", "*.srt", "*.ass", "*.ssa", "*.vtt", "*.sup", "*.idx", "*.sub",
        ],
    };

    private static readonly FilePickerFileType AllFiles = new("All files") { Patterns = ["*"] };

    public async Task<IReadOnlyList<string>> PickSourceFilesAsync()
    {
        var window = await ActivatedWindowAsync();
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add source files",
            AllowMultiple = true,
            FileTypeFilter = [SourceFiles, AllFiles],
        });
        return [.. files.Select(file => file.TryGetLocalPath()).OfType<string>()];
    }

    public async Task<string?> PickOutputFileAsync(string suggestedPath, OutputFormatInfo format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var window = await ActivatedWindowAsync();
        var directory = Path.GetDirectoryName(suggestedPath);
        var folder = directory is { Length: > 0 } && Directory.Exists(directory)
            ? await window.StorageProvider.TryGetFolderFromPathAsync(directory)
            : null;

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Output file",
            SuggestedFileName = Path.GetFileName(suggestedPath),
            SuggestedStartLocation = folder,
            DefaultExtension = format.Extension.TrimStart('.'),
            ShowOverwritePrompt = false,
            FileTypeChoices = [new FilePickerFileType(format.Label) { Patterns = ["*" + format.Extension] }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var owner = windowAccessor();
        var confirm = new Button { Content = confirmText, Classes = { "accent" }, IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, confirm },
                    },
                },
            },
        };

        var confirmed = false;
        confirm.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
        return confirmed;
    }

    public async Task ShowInFolderAsync(string filePath)
    {
        var window = windowAccessor();
        if (Path.GetDirectoryName(filePath) is not { Length: > 0 } directory || !Directory.Exists(directory))
            return;
        await window.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(directory));
    }

    public async Task SetClipboardTextAsync(string text)
    {
        if (windowAccessor().Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private async Task<Window> ActivatedWindowAsync()
    {
        var window = windowAccessor();
        // macOS only focuses native pickers for an active window; let the activation settle first.
        window.Activate();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return window;
    }
}
