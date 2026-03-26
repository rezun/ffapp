using System.Globalization;
using Avalonia.Data.Converters;

namespace FFApp.Converters;

public sealed class EnumToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return false;

        return value.Equals(parameter is string s ? Enum.Parse(value.GetType(), s) : parameter);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null)
            return parameter is string s ? Enum.Parse(targetType, s) : parameter;

        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
