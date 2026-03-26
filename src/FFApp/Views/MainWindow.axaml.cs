using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
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
    }

    private async void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var settings = await vm.SettingsLoadedTask;
        ApplyWindowGeometry(settings.WindowGeometry);
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
