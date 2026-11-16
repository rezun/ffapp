using Avalonia.Controls;
using Avalonia.Input;
using System.Reflection;
using Avalonia.Platform.Storage;
using FFApp.Infrastructure;
using FFRux.Infrastructure;
using FFRux.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFRux.Views;

public partial class MainWindow : Window
{
    private void OnToolsAndComponents(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            new ToolsWindow { DataContext = vm, Logger = Logger }.Show(this);
    }
    private bool _closeConfirmed;

    /// <summary>Set by <see cref="App"/> once the service provider exists, which is after the window is created.</summary>
    public ILogger Logger { get; set; } = NullLogger<MainWindow>.Instance;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // NativeMenuItem is not a control, so x:Name generates no field; find the version entry instead.
        var versionItem = NativeMenu.GetMenu(this)?.Items.OfType<NativeMenuItem>()
            .FirstOrDefault(item => Equals(item.Header, "Info"))?.Menu?.Items.OfType<NativeMenuItem>()
            .FirstOrDefault(item => Equals(item.Header, "FFRux"));
        if (versionItem is not null && Assembly.GetExecutingAssembly().GetName().Version is { } version)
            versionItem.Header = $"FFRux v{version.ToString(3)}";
    }

    // async void: NativeMenuItem.Click is a synchronous event; failures are caught and logged.
    private async void OnOpenLogsFolder(object? sender, EventArgs e)
    {
        try
        {
            // The folder only exists once something was logged; create it so the file manager can open it.
            var folder = Directory.CreateDirectory(AppPaths.LogDirectory(FFRuxPaths.Identity));
            await Launcher.LaunchDirectoryInfoAsync(folder);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Opening the logs folder failed");
        }
    }

    // async void: Avalonia lifecycle override — it must return void. Failures are caught and logged.
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || DataContext is not MainWindowViewModel { IsRunning: true } vm)
            return;

        e.Cancel = true;
        try
        {
            if (!await vm.PrepareToCloseAsync())
                return;
            _closeConfirmed = true;
            Close();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Closing FFRux failed");
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var canAdd = DataContext is MainWindowViewModel { IsEditable: true } && e.DataTransfer.TryGetFiles() is not null;
        e.DragEffects = canAdd ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // async void: Avalonia event handler bridge — the drop event cannot await. Failures are caught and logged.
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || e.DataTransfer.TryGetFiles() is not { } files)
            return;

        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
        try
        {
            await vm.OpenFilesAsync(paths);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Adding dropped files failed");
        }
    }
}
