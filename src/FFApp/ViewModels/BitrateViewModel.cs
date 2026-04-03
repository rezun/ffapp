using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FFApp.ViewModels;

public partial class BitrateViewModel : ObservableObject
{
    [ObservableProperty]
    private decimal _videoBitrateKbps = 3000;

    [ObservableProperty]
    private bool _twoPass;

    public string VideoBitrateKbpsText
    {
        get => ((int)VideoBitrateKbps).ToString(CultureInfo.InvariantCulture);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                OnPropertyChanged(nameof(VideoBitrateKbpsText));
                return;
            }

            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                OnPropertyChanged(nameof(VideoBitrateKbpsText));
                return;
            }

            var clamped = Math.Clamp(parsed, 100, 100000);
            if (VideoBitrateKbps != clamped)
                VideoBitrateKbps = clamped;
            else
                OnPropertyChanged(nameof(VideoBitrateKbpsText));
        }
    }

    [ObservableProperty]
    private string? _estimatedFileSizeText;

    public bool HasEstimatedFileSize => !string.IsNullOrEmpty(EstimatedFileSizeText);

    partial void OnEstimatedFileSizeTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasEstimatedFileSize));
    }

    partial void OnVideoBitrateKbpsChanged(decimal value)
    {
        OnPropertyChanged(nameof(VideoBitrateKbpsText));
    }
}
