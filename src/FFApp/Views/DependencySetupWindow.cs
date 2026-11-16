using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace FFApp.Views;

public sealed class DependencySetupWindow : Window
{
    public DependencySetupWindow()
    {
        // Handle dismissal before a focused path field can consume the key event.
        AddHandler(KeyDownEvent, OnDismissKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnDismissKeyDown(object? sender, KeyEventArgs e)
    {
        var closeModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if ((e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            || (e.Key == Key.W && e.KeyModifiers == closeModifier))
        {
            e.Handled = true;
            Close();
        }
    }
}
