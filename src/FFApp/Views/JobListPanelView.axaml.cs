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
        vm.UpdateSelection(JobList.Selection.SelectedItems.Cast<JobItemViewModel>().ToList());
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
}
