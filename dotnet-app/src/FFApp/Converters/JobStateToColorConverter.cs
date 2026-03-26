using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using FFApp.Core.Models;

namespace FFApp.Converters;

public sealed class JobStateToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not JobState state)
            return Brushes.Gray;

        var key = state switch
        {
            JobState.Pending => "JobStatePendingBrush",
            JobState.Running => "JobStateRunningBrush",
            JobState.Completed => "JobStateCompletedBrush",
            JobState.Failed => "JobStateFailedBrush",
            JobState.Canceled => "JobStateCanceledBrush",
            _ => "JobStatePendingBrush",
        };

        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var resource) == true
            && resource is IBrush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
