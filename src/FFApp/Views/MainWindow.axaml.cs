using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using FFApp.Core.Models;
using FFApp.ViewModels;

namespace FFApp.Views;

public partial class MainWindow : Window
{
    private bool _closingSaved;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        Loaded += OnLoaded;

        // Set version in Info menu
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionStr = version is not null ? $"{version.Major}.{version.Minor}.{version.Build}" : "dev";
        var versionItem = FindMenuItem("Info", "FFApp");
        if (versionItem is not null)
            versionItem.Header = $"FFApp v{versionStr}";
    }

    private async void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var settings = await vm.SettingsLoadedTask;
        ApplyWindowGeometry(settings.WindowGeometry);
        SetTheme(settings.ThemePreference, persist: false);
    }

    private void ApplyWindowGeometry(WindowGeometry geo)
    {
        if (geo.Width is { } w && geo.Height is { } h && w > 0 && h > 0)
        {
            Width = w;
            Height = h;
        }

        if (geo.X is { } x && geo.Y is { } y)
        {
            var savedRect = new PixelRect(x, y, (int)Width, (int)Height);
            foreach (var screen in Screens.All)
            {
                if (screen.Bounds.Intersects(savedRect))
                {
                    Position = new PixelPoint(x, y);
                    break;
                }
            }
        }

        if (geo.IsMaximized)
            WindowState = WindowState.Maximized;
    }

    // NativeMenu click handlers
    public void OnThemeSystem(object? sender, EventArgs e) => SetTheme(ThemePreference.System);
    public void OnThemeDark(object? sender, EventArgs e) => SetTheme(ThemePreference.Dark);
    public void OnThemeLight(object? sender, EventArgs e) => SetTheme(ThemePreference.Light);

    private void SetTheme(ThemePreference preference, bool persist = true)
    {
        if (App.Current is App app)
            app.SetThemeVariant(preference);

        // Update radio check states
        var viewMenu = FindMenu("View");
        if (viewMenu is not null)
        {
            foreach (var item in viewMenu.Items.OfType<NativeMenuItem>())
                item.IsChecked = item.Header?.ToString() == preference.ToString();
        }

        if (persist)
            PersistThemePreference(preference);
    }

    private async void PersistThemePreference(ThemePreference preference)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var settings = (vm.LoadedSettings ?? new AppSettings()) with { ThemePreference = preference };
        vm.LoadedSettings = settings;
        await vm.SettingsService.SaveAsync(settings);
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

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var storageItems = e.Data.GetFiles();
        if (storageItems is null)
            return;

        var paths = new List<string>();
        foreach (var item in storageItems)
        {
            if (item.Path is { } uri)
                paths.Add(uri.LocalPath);
        }

        if (paths.Count > 0)
            vm.HandleFileDrop(paths);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_closingSaved || DataContext is not MainWindowViewModel vm)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        var isMaximized = WindowState == WindowState.Maximized;

        var geo = new WindowGeometry(
            X: Position.X,
            Y: Position.Y,
            Width: (int)Width,
            Height: (int)Height,
            IsMaximized: isMaximized);

        var settings = (vm.LoadedSettings ?? new AppSettings()) with { WindowGeometry = geo };

        if (vm.CurrentView is WorkspaceViewModel)
        {
            var workspaceView = this.GetVisualDescendants().OfType<WorkspaceView>().FirstOrDefault();
            if (workspaceView is not null)
            {
                var layout = workspaceView.GetSplitterLayout();
                settings = settings with { SplitterLayout = layout };
            }
        }

        vm.LoadedSettings = settings;
        await vm.SettingsService.SaveAsync(settings);

        _closingSaved = true;
        Close();
    }
}
