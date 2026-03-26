using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;

namespace FFApp.ViewModels;

public partial class AudioViewModel : ObservableObject
{
    [ObservableProperty]
    private AudioMode _selectedAudioMode = AudioMode.Copy;

    [ObservableProperty]
    private AudioCodec? _selectedAudioCodec = AudioCodec.Aac;

    [ObservableProperty]
    private int _selectedBitrateOption = 128;

    [ObservableProperty]
    private int _customBitrateKbps = 128;

    [ObservableProperty]
    private bool _remixToStereo;

    [ObservableProperty]
    private IReadOnlyList<AudioCodec> _availableAudioCodecs = [];

    /// <summary>Predefined bitrate options in kbps, plus -1 as the "Custom" sentinel.</summary>
    public static IReadOnlyList<int> AvailableBitrateOptions { get; } =
        [32, 64, 96, 128, 160, 192, 256, 320, -1];

    public bool IsCustomBitrate => SelectedBitrateOption == -1;

    /// <summary>Effective bitrate in bps for the model layer.</summary>
    public int AudioBitrate
    {
        get => IsCustomBitrate ? CustomBitrateKbps * 1000 : SelectedBitrateOption * 1000;
        set
        {
            var kbps = value / 1000;
            if (AvailableBitrateOptions.Contains(kbps))
            {
                SelectedBitrateOption = kbps;
            }
            else
            {
                SelectedBitrateOption = -1;
                CustomBitrateKbps = kbps;
            }
        }
    }

    partial void OnSelectedBitrateOptionChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomBitrate));
        OnPropertyChanged(nameof(AudioBitrate));
    }

    partial void OnCustomBitrateKbpsChanged(int value)
    {
        if (IsCustomBitrate)
            OnPropertyChanged(nameof(AudioBitrate));
    }

    public bool IsReencodeMode => SelectedAudioMode == AudioMode.Reencode;

    partial void OnSelectedAudioModeChanged(AudioMode value)
    {
        OnPropertyChanged(nameof(IsReencodeMode));
    }
}
