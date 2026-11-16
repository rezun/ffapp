using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FFApp.Core.Models;
using FFApp.ViewModels;

namespace FFApp.Views;

public partial class WorkspaceView : UserControl
{
    private const int DefaultPreviewHeight = 350;
    private int _savedPreviewHeight = DefaultPreviewHeight;

    private WorkspaceViewModel? _previousVm;

    public WorkspaceView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        VideoPreviewPanel.LayoutUpdated += OnVideoPreviewLayoutUpdated;
    }

    /// <summary>
    /// Keeps the preview row at least as tall as the expanded preview needs, so the
    /// splitter cannot squeeze the header and controls under the native video surface.
    /// </summary>
    private void OnVideoPreviewLayoutUpdated(object? sender, EventArgs e)
    {
        var row = PreviewSettingsGrid.RowDefinitions[0];
        var minHeight = row.Height.GridUnitType == GridUnitType.Pixel
            ? Math.Ceiling(VideoPreviewPanel.NaturalHeight)
            : 0;

        if (Math.Abs(row.MinHeight - minHeight) >= 1)
            row.MinHeight = minHeight;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_previousVm is not null)
            _previousVm.VideoPreview.PropertyChanged -= OnVideoPreviewPropertyChanged;

        _previousVm = DataContext as WorkspaceViewModel;

        if (_previousVm is not null)
            _previousVm.VideoPreview.PropertyChanged += OnVideoPreviewPropertyChanged;
    }

    private void OnVideoPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VideoPreviewViewModel.IsExpanded))
            return;

        var isExpanded = sender is VideoPreviewViewModel vp && vp.IsExpanded;
        if (isExpanded)
        {
            PreviewSettingsGrid.RowDefinitions[0].Height =
                new GridLength(_savedPreviewHeight, GridUnitType.Pixel);
        }
        else
        {
            // Save current height before collapsing
            var actual = (int)PreviewSettingsGrid.RowDefinitions[0].ActualHeight;
            if (actual > 0)
                _savedPreviewHeight = actual;

            PreviewSettingsGrid.RowDefinitions[0].Height = GridLength.Auto;
        }
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
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

        if (layout.PreviewHeight > 0)
            _savedPreviewHeight = layout.PreviewHeight;

        if (layout.QueueSplitterSizes is { Length: 2 })
        {
            var jobListPanel = this.GetVisualDescendants().OfType<JobListPanelView>().FirstOrDefault();
            jobListPanel?.RestoreSplitterSizes(layout.QueueSplitterSizes);
        }
    }

    public SplitterLayout GetSplitterLayout()
    {
        var col0 = (int)MainGrid.ColumnDefinitions[0].ActualWidth;
        var col2 = (int)MainGrid.ColumnDefinitions[2].ActualWidth;
        var col4 = (int)MainGrid.ColumnDefinitions[4].ActualWidth;

        // If currently expanded, read the live height; otherwise use the saved value
        var previewHeight = PreviewSettingsGrid.RowDefinitions[0].Height.GridUnitType == GridUnitType.Pixel
            ? (int)PreviewSettingsGrid.RowDefinitions[0].ActualHeight
            : _savedPreviewHeight;

        var jobListPanel = this.GetVisualDescendants().OfType<JobListPanelView>().FirstOrDefault();
        var queueSizes = jobListPanel?.GetSplitterSizes() ?? [];

        return new SplitterLayout
        {
            MainSplitterSizes = [col0, col2, col4],
            QueueSplitterSizes = queueSizes,
            PreviewHeight = previewHeight,
        };
    }
}
