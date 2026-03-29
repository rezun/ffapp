using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;

namespace FFApp.ViewModels;

public partial class FileSizeViewModel : ObservableObject
{
    [ObservableProperty]
    private decimal _targetSizeMb = FileSizeDefaults.TargetSizeMb;

    [ObservableProperty]
    private bool _twoPass;

    [ObservableProperty]
    private double _muxingOverheadRatio = FileSizeDefaults.MuxingOverheadRatio;

    [ObservableProperty]
    private int _absoluteOverheadBytes = FileSizeDefaults.AbsoluteOverheadBytes;

    [ObservableProperty]
    private string? _videoBitrateText;

    public bool HasVideoBitrate => !string.IsNullOrEmpty(VideoBitrateText);

    partial void OnVideoBitrateTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasVideoBitrate));
    }

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
