using Avalonia.Controls;
using Avalonia.Input;
using FFApp.ViewModels;

namespace FFApp.Views;

public partial class QueuePanelView : UserControl
{
    public QueuePanelView()
    {
        InitializeComponent();
        QueueList.KeyDown += OnListKeyDown;
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back
            && DataContext is QueuePanelViewModel vm
            && vm.RemoveSelectedCommand.CanExecute(null))
        {
            vm.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }
}
