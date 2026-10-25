using System.Globalization;
using Avalonia.Data.Converters;

namespace FFApp.Converters;

/// <summary>Formats the decimal seconds of a numeric editor as a source timecode.</summary>
public sealed class SecondsToTimecodeConverter : IValueConverter
{
    // NumericUpDown uses Convert for text-to-value and ConvertBack for value-to-text.
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not decimal seconds) return "";
        var milliseconds = decimal.Round(seconds * 1000, 0, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture,
            $"{decimal.Floor(milliseconds / 3600000):00}:{decimal.Floor(milliseconds / 60000) % 60:00}:{decimal.Floor(milliseconds / 1000) % 60:00}.{milliseconds % 1000:000}");
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text)
        {
            var parts = text.Trim().Split(':');
            if (parts.Length == 3
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                && decimal.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
                && hours >= 0 && minutes is >= 0 and < 60 && seconds is >= 0 and < 60)
                return hours * 3600m + minutes * 60m + seconds;
        }
        throw new FormatException("Enter a source position as hh:mm:ss.fff.");
    }
}
