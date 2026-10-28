using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FFRux.Converters;

/// <summary>Turns a source's marker index into one of the SourceMarker brushes from the shared tokens.</summary>
public sealed class MarkerBrushConverter : IValueConverter
{
    public static MarkerBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var index = value is int number ? number : 0;
        var key = string.Create(CultureInfo.InvariantCulture, $"SourceMarker{index}Brush");
        return Application.Current?.TryFindResource(key, out var brush) == true ? brush as IBrush : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
