using System.Globalization;
using Avalonia.Data.Converters;

namespace FFApp.Converters;

public sealed class EnumToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return false;

        if (parameter is string s && Enum.TryParse(value.GetType(), s, out var parsed))
            return value.Equals(parsed);

        return value.Equals(parameter);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null)
        {
            if (parameter is string s && Enum.TryParse(targetType, s, out var parsed))
                return parsed;
            return parameter;
        }

        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
