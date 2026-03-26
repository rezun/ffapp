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
        if (layout.MainSplitterSizes is { Length: 2 } mainSizes)
        {
            var total = mainSizes[0] + mainSizes[1];
            if (total > 0)
            {
                MainGrid.ColumnDefinitions[0].Width = new GridLength(mainSizes[0], GridUnitType.Star);
                MainGrid.ColumnDefinitions[2].Width = new GridLength(mainSizes[1], GridUnitType.Star);
            }
        }

        if (layout.RightSplitterSizes is { Length: 2 } rightSizes)
        {
            var total = rightSizes[0] + rightSizes[1];
            if (total > 0)
            {
                RightGrid.RowDefinitions[0].Height = new GridLength(rightSizes[0], GridUnitType.Star);
                RightGrid.RowDefinitions[2].Height = new GridLength(rightSizes[1], GridUnitType.Star);
            }
        }
    }

    public SplitterLayout GetSplitterLayout()
    {
        var mainCol0 = (int)MainGrid.ColumnDefinitions[0].ActualWidth;
        var mainCol2 = (int)MainGrid.ColumnDefinitions[2].ActualWidth;

        var rightRow0 = (int)RightGrid.RowDefinitions[0].ActualHeight;
        var rightRow2 = (int)RightGrid.RowDefinitions[2].ActualHeight;

        return new SplitterLayout
        {
            MainSplitterSizes = [mainCol0, mainCol2],
            RightSplitterSizes = [rightRow0, rightRow2],
        };
    }
}
