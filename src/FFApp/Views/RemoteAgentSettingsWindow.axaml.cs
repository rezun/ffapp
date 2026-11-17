using Avalonia.Controls;
using Avalonia.Interactivity;

using FFApp.ViewModels;

namespace FFApp.Views;

public sealed partial class RemoteAgentSettingsWindow : Window
{
    public RemoteAgentSettingsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is RemoteAgentSettingsViewModel vm)
            vm.Saved += Close;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is RemoteAgentSettingsViewModel vm)
        {
            vm.Saved -= Close;
            vm.Dispose();
        }
        base.OnClosed(e);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
