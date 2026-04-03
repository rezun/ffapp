using Avalonia.Controls;
using Avalonia.Input;
using FFApp.ViewModels;

namespace FFApp.Views;

public partial class SourceListPanelView : UserControl
{
    public SourceListPanelView()
    {
        InitializeComponent();
        SourceList.SelectionChanged += OnSelectionChanged;
        SourceList.KeyDown += OnListKeyDown;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not SourceListPanelViewModel vm) return;
        vm.UpdateSelection(SourceList.Selection.SelectedItems
            .Cast<SourceFileViewModel>().ToList());
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back
            && DataContext is SourceListPanelViewModel vm
            && vm.RemoveSelectedCommand.CanExecute(null))
        {
            vm.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }
}
