using Avalonia.Controls;
using Avalonia.Input;

namespace FFApp.Controls;

public class NoWheelComboBox : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!IsFocused) { e.Handled = false; return; }
        base.OnPointerWheelChanged(e);
    }
}
