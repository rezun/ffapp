using Avalonia.Controls;
using Avalonia.Input;

namespace FFApp.Controls;

public class NoWheelNumericUpDown : NumericUpDown
{
    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!IsFocused) { e.Handled = false; return; }
        base.OnPointerWheelChanged(e);
    }
}
