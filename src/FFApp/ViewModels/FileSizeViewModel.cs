using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FFApp.ViewModels;

public partial class FileSizeViewModel : ObservableObject
{
    [ObservableProperty]
    private decimal _targetSizeMb = 50;

    [ObservableProperty]
    private bool _twoPass;

    [ObservableProperty]
    private double _muxingOverheadRatio = 0.015;

    public string TargetSizeMbText
    {
        get => ((int)TargetSizeMb).ToString(CultureInfo.InvariantCulture);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                OnPropertyChanged(nameof(TargetSizeMbText));
                return;
            }

            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                OnPropertyChanged(nameof(TargetSizeMbText));
                return;
            }

            var clamped = Math.Clamp(parsed, 1, 10000);
            if (TargetSizeMb != clamped)
                TargetSizeMb = clamped;
            else
                OnPropertyChanged(nameof(TargetSizeMbText));
        }
    }

    partial void OnTargetSizeMbChanged(decimal value)
    {
        OnPropertyChanged(nameof(TargetSizeMbText));
    }
}
