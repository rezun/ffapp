using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using FFApp.ViewModels;

namespace FFApp.Views;

public partial class JobListPanelView : UserControl
{
    public JobListPanelView()
    {
        InitializeComponent();
        JobList.SelectionChanged += OnSelectionChanged;
        JobList.KeyDown += OnListKeyDown;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not JobListPanelViewModel vm) return;
        vm.UpdateSelection(JobList.Selection.SelectedItems
            .Cast<JobItemViewModel>().ToList());
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back
            && DataContext is JobListPanelViewModel vm
            && vm.RemoveSelectedCommand.CanExecute(null))
        {
            vm.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    public ImmutableArray<int> GetSplitterSizes()
    {
        var row0 = (int)QueueDetailGrid.RowDefinitions[0].ActualHeight;
        var row2 = (int)QueueDetailGrid.RowDefinitions[2].ActualHeight;
        return [row0, row2];
    }

    public void RestoreSplitterSizes(ImmutableArray<int> sizes)
    {
        if (sizes is not { Length: 2 }) return;
        var total = sizes[0] + sizes[1];
        if (total <= 0) return;

        QueueDetailGrid.RowDefinitions[0].Height = new GridLength(sizes[0], GridUnitType.Star);
        QueueDetailGrid.RowDefinitions[2].Height = new GridLength(sizes[1], GridUnitType.Star);
    }
}
