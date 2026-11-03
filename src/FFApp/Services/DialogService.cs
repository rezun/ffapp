using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace FFApp.Services;

public sealed class DialogService(Func<Window?> windowAccessor) : IDialogService
{
    public async Task<string?> OpenFileAsync(string title, string? filter = null)
    {
        var window = await GetWindowAsync();

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ParseFilter(filter),
        };

        var result = await window.StorageProvider.OpenFilePickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task OpenWithDefaultAppAsync(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (!File.Exists(filePath))
            return;
        var window = await GetWindowAsync();
        await window.Launcher.LaunchFileInfoAsync(new FileInfo(filePath));
    }

    public async Task<string[]?> OpenFilesAsync(string title, string? filter = null)
    {
        var window = await GetWindowAsync();

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = ParseFilter(filter),
        };

        var result = await window.StorageProvider.OpenFilePickerAsync(options);
        if (result.Count == 0)
            return null;

        var paths = new string[result.Count];
        for (var i = 0; i < result.Count; i++)
            paths[i] = result[i].TryGetLocalPath() ?? result[i].Name;

        return paths;
    }

    public async Task<string?> OpenFolderAsync(string title)
    {
        var window = await GetWindowAsync();

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        };

        var result = await window.StorageProvider.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string? filter = null)
    {
        var window = await GetWindowAsync();

        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = ParseFilter(filter),
        };

        var result = await window.StorageProvider.SaveFilePickerAsync(options);
        return result?.TryGetLocalPath();
    }

    public async Task<string?> PromptTextAsync(string title, string message, string? defaultValue = null)
    {
        var window = GetWindow();

        var textBox = new TextBox
        {
            Text = defaultValue ?? string.Empty,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
        };

        var dialog = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Children =
                {
                    new TextBlock { Text = message },
                    textBox,
                    CreateButtonPanel(out var okButton, out var cancelButton),
                },
            },
        };

        string? result = null;

        okButton.Click += (_, _) => { result = textBox.Text; dialog.Close(); };
        cancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(window);
        return result;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var window = GetWindow();

        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    CreateOkPanel(out var okButton),
                },
            },
        };

        okButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(window);
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        await ShowMessageAsync(title, message);
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var window = GetWindow();

        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    CreateButtonPanel(out var okButton, out var cancelButton),
                },
            },
        };

        var confirmed = false;

        okButton.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(window);
        return confirmed;
    }

    public async Task<string?> GetClipboardTextAsync()
    {
        var window = GetWindow();
        var clipboard = window.Clipboard;
        if (clipboard is null)
            return null;

        return await clipboard.TryGetTextAsync();
    }

    public async Task SetClipboardTextAsync(string text)
    {
        var window = GetWindow();
        var clipboard = window.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    // --- Private helpers ---

    private Window GetWindow() =>
        windowAccessor() ?? throw new InvalidOperationException("No window available.");

    private async Task<Window> GetWindowAsync()
    {
        var window = windowAccessor() ?? throw new InvalidOperationException("No window available.");
        // Ensure the window is activated so macOS gives focus to native file pickers.
        // We must yield to the event loop so the activation is fully processed
        // before the native picker opens.
        window.Activate();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return window;
    }

    private static StackPanel CreateButtonPanel(out Button okButton, out Button cancelButton)
    {
        okButton = new Button
        {
            Content = "OK",
            Width = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        cancelButton = new Button
        {
            Content = "Cancel",
            Width = 80,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 16, 0, 0),
            Children = { okButton, cancelButton },
        };
    }

    private static StackPanel CreateOkPanel(out Button okButton)
    {
        okButton = new Button
        {
            Content = "OK",
            Width = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 16, 0, 0),
            Children = { okButton },
        };
    }

    private static List<FilePickerFileType>? ParseFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return null;

        // Filter format: "Video Files|*.mp4;*.mkv|All Files|*.*"
        var parts = filter.Split('|');
        var types = new List<FilePickerFileType>();

        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var name = parts[i].Trim();
            var patterns = parts[i + 1]
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .ToList();

            types.Add(new FilePickerFileType(name) { Patterns = patterns });
        }

        return types.Count > 0 ? types : null;
    }
}
