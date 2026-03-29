using Avalonia.Controls;
using Avalonia.Input;

namespace FFApp.Controls;

public class NumericTextBox : TextBox
{
    private bool _isSanitizing;

    protected override Type StyleKeyOverride => typeof(TextBox);

    public NumericTextBox()
    {
        TextChanging += OnTextChanging;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text) && ContainsNonDigits(e.Text))
        {
            e.Handled = true;
            return;
        }

        base.OnTextInput(e);
    }

    private void OnTextChanging(object? sender, TextChangingEventArgs e)
    {
        if (_isSanitizing)
            return;

        var text = Text ?? string.Empty;
        var sanitized = KeepDigitsOnly(text);
        if (sanitized == text)
            return;

        _isSanitizing = true;
        try
        {
            Text = sanitized;
            CaretIndex = sanitized.Length;
        }
        finally
        {
            _isSanitizing = false;
        }
    }

    private static bool ContainsNonDigits(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsDigit(c))
                return true;
        }

        return false;
    }

    private static string KeepDigitsOnly(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var chars = new char[text.Length];
        var count = 0;

        foreach (var c in text)
        {
            if (char.IsDigit(c))
                chars[count++] = c;
        }

        return count == text.Length ? text : new string(chars, 0, count);
    }
}
