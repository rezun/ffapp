using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FFApp.Core.Models;
using FFApp.Infrastructure;
using FFApp.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFApp.Views;

public partial class MainWindow : Window
{
    private bool _closingSaved;
    private bool _isClosing;
    internal bool IsCloseSaved => _closingSaved;

    /// <summary>Set by <see cref="App"/> once the service provider exists, which is after the window is created.</summary>
    public ILogger Logger { get; set; } = NullLogger<MainWindow>.Instance;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Loaded += OnLoaded;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionStr = version is not null ? $"{version.Major}.{version.Minor}.{version.Build}" : "dev";
        var versionItem = FindMenuItem("Info", "FFApp");
        if (versionItem is not null)
            versionItem.Header = $"FFApp v{versionStr}";
    }

    // async void: Avalonia event handler bridge — cannot return Task.
    private async void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        try
        {
            await vm.InitializeAsync();

            var settings = await vm.SettingsLoadedTask;
            SetTheme(settings.ThemePreference, persist: false);

            Dispatcher.UIThread.Post(Activate);
            using var updateCheck = new CancellationTokenSource();
            void CancelUpdateCheck(object? sender, EventArgs args) => updateCheck.Cancel();
            Closed += CancelUpdateCheck;
            try { await vm.Updates.CheckAsync(manual: false, updateCheck.Token); }
            finally { Closed -= CancelUpdateCheck; }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Window initialization failed");
        }
    }

    public void OnThemeSystem(object? sender, EventArgs e) => SetTheme(ThemePreference.System);

    // async void: NativeMenuItem.Click is synchronous; failures are caught at this UI boundary.
    private async void OnToolsAndComponents(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        try
        {
            var setup = await vm.CreateDependencySetupAsync();
            var screen = Screens.ScreenFromWindow(this);
            var availableHeight = screen is null ? 900 : screen.WorkingArea.Height / screen.Scaling;
            await new DependencySetupWindow
            {
                Title = "Tools and components", Width = 720, SizeToContent = SizeToContent.Height,
                MinWidth = 600, MinHeight = 320, MaxHeight = Math.Max(320, availableHeight - 64),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new DependencySetupView { DataContext = setup },
            }.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Opening tool configuration failed");
        }
    }

    private void OnComparisonStorage(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.RefreshComparisonStorageCommand.Execute(null);
        new ComparisonStorageWindow { DataContext = vm }.Show(this);
    }
    // async void: NativeMenuItem.Click is a synchronous event; failures are caught and logged.
    private async void OnOpenLogsFolder(object? sender, EventArgs e)
    {
        try
        {
            // The folder only exists once something was logged; create it so the file manager can open it.
            var folder = Directory.CreateDirectory(AppPaths.LogDirectory(FFAppPaths.Identity));
            await Launcher.LaunchDirectoryInfoAsync(folder);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Opening the logs folder failed");
        }
    }

    public void OnThemeDark(object? sender, EventArgs e) => SetTheme(ThemePreference.Dark);
    public void OnThemeLight(object? sender, EventArgs e) => SetTheme(ThemePreference.Light);

    private void SetTheme(ThemePreference preference, bool persist = true)
    {
        if (App.Current is App app)
            app.SetThemeVariant(preference);

        var viewMenu = FindMenu("View");
        if (viewMenu is not null)
        {
            foreach (var item in viewMenu.Items.OfType<NativeMenuItem>())
                item.IsChecked = item.Header?.ToString() == preference.ToString();
        }

        if (persist)
            PersistThemePreference(preference);
    }

    // async void: Called from synchronous SetTheme which is bound to menu handlers — fire-and-forget
    // is unavoidable here. Wrapped in try/catch so save failures don't crash the app.
    private async void PersistThemePreference(ThemePreference preference)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        try
        {
            vm.UpdateSettings(s => s with { ThemePreference = preference });
            await vm.SettingsService.SaveAsync(vm.LoadedSettings!);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to persist theme preference");
        }
    }

    private NativeMenu? FindMenu(string header)
    {
        var menu = NativeMenu.GetMenu(this);
        return menu?.Items.OfType<NativeMenuItem>()
            .FirstOrDefault(i => i.Header?.ToString() == header)?.Menu;
    }

    private NativeMenuItem? FindMenuItem(string menuHeader, string itemHeader)
    {
        return FindMenu(menuHeader)?.Items.OfType<NativeMenuItem>()
            .FirstOrDefault(i => i.Header?.ToString() == itemHeader);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return;

        var paths = new List<string>();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                paths.Add(path);
        }

        if (paths.Count > 0)
            vm.HandleFileDrop(paths);
    }

    // async void: Avalonia lifecycle override — must return void.
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_closingSaved || DataContext is not MainWindowViewModel)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        await CloseSavedAsync();
    }

    public async Task<bool> CloseSavedAsync(bool closeWindow = true)
    {
        if (_closingSaved) return true;
        if (_isClosing) return false;
        if (DataContext is not MainWindowViewModel vm)
        {
            Close();
            return !IsVisible;
        }
        _isClosing = true;

        try
        {
            if (!await vm.CloseComparisonsAsync())
            {
                _isClosing = false;
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to close comparisons");
            _isClosing = false;
            return false;
        }

        try
        {
            var isMaximized = WindowState == WindowState.Maximized;

            var geo = new WindowGeometry(
                X: Position.X,
                Y: Position.Y,
                Width: (int)Width,
                Height: (int)Height,
                IsMaximized: isMaximized);

            vm.UpdateSettings(s => s with { WindowGeometry = geo });

            if (vm.CurrentView is WorkspaceViewModel workspace)
            {
                if (workspace.CurrentSettings is { } outputSettings)
                    vm.UpdateSettings(s => s with { LastUsedSettings = outputSettings });

                vm.UpdateSettings(s => s with
                {
                    LastUsedPresetName = workspace.Preset.SelectedPreset?.Name,
                    OutputLocation = workspace.OutputPath.Location,
                });

                var workspaceView = this.GetVisualDescendants().OfType<WorkspaceView>().FirstOrDefault();
                if (workspaceView is not null)
                {
                    var layout = workspaceView.GetSplitterLayout();
                    vm.UpdateSettings(s => s with { SplitterLayout = layout });
                }
            }

            await vm.SettingsService.SaveAsync(vm.LoadedSettings!);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save settings on close");
            if (!closeWindow) throw;
        }
        finally
        {
            if (closeWindow)
            {
                (vm.CurrentView as WorkspaceViewModel)?.VideoPreview.Dispose();
                _closingSaved = true;
                Close();
            }
            else
            {
                _isClosing = false;
            }
        }
        return true;
    }
}
