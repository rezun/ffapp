using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FFApp.Core.Models;
using FFApp.ViewModels;

namespace FFApp.Views;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Walk up to MainWindow to get settings
        var mainWindow = this.GetVisualAncestors().OfType<MainWindow>().FirstOrDefault();
        if (mainWindow?.DataContext is not MainWindowViewModel vm)
            return;

        var layout = vm.LoadedSettings?.SplitterLayout;
        if (layout is null)
            return;

        RestoreSplitterLayout(layout);
    }

    private void RestoreSplitterLayout(SplitterLayout layout)
    {
        // 3-column layout: columns 0 (Source), 2 (Settings), 4 (Jobs)
        if (layout.MainSplitterSizes is { Length: 3 } mainSizes)
        {
            var total = mainSizes[0] + mainSizes[1] + mainSizes[2];
            if (total > 0)
            {
                MainGrid.ColumnDefinitions[0].Width = new GridLength(mainSizes[0], GridUnitType.Star);
                MainGrid.ColumnDefinitions[2].Width = new GridLength(mainSizes[1], GridUnitType.Star);
                MainGrid.ColumnDefinitions[4].Width = new GridLength(mainSizes[2], GridUnitType.Star);
            }
        }
    }

    public SplitterLayout GetSplitterLayout()
    {
        var col0 = (int)MainGrid.ColumnDefinitions[0].ActualWidth;
        var col2 = (int)MainGrid.ColumnDefinitions[2].ActualWidth;
        var col4 = (int)MainGrid.ColumnDefinitions[4].ActualWidth;

        return new SplitterLayout
        {
            MainSplitterSizes = [col0, col2, col4],
        };
    }
}
