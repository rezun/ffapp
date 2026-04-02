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
    private int _selectedAudioChannels;

    [ObservableProperty]
    private IReadOnlyList<int> _availableAudioChannelOptions = [0, 1, 2];

    private static readonly int[] AllChannelOptions = [0, 1, 2, 6];

    [ObservableProperty]
    private IReadOnlyList<AudioCodec> _availableAudioCodecs = [];

    /// <summary>Predefined bitrate options in kbps, plus -1 as the "Custom" sentinel.</summary>
    public static IReadOnlyList<int> AvailableBitrateOptions { get; } =
        [32, 64, 96, 128, 160, 192, 256, 320, -1];

    public bool IsCustomBitrate => SelectedBitrateOption == -1;

    public string CustomBitrateText
    {
        get => CustomBitrateKbps.ToString();
        set
        {
            if (int.TryParse(value, out var kbps) && kbps > 0)
                CustomBitrateKbps = kbps;
        }
    }

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
        {
            OnPropertyChanged(nameof(AudioBitrate));
            OnPropertyChanged(nameof(CustomBitrateText));
        }
    }

    private int _maxSourceChannels;

    public int MaxSourceChannels
    {
        get => _maxSourceChannels;
        set
        {
            if (!SetProperty(ref _maxSourceChannels, value))
                return;

            // Show options that are valid downmixes (less than source channels),
            // plus stereo is always available (mono→stereo is a valid expansion).
            AvailableAudioChannelOptions = AllChannelOptions
                .Where(ch => ch == 0 || ch <= 2 || ch < value)
                .ToArray();

            if (!AvailableAudioChannelOptions.Contains(SelectedAudioChannels))
                SelectedAudioChannels = 0;
        }
    }

    [ObservableProperty]
    private bool _isRemoveAllowed = true;

    public bool IsReencodeMode => SelectedAudioMode == AudioMode.Reencode;

    partial void OnSelectedAudioModeChanged(AudioMode value)
    {
        OnPropertyChanged(nameof(IsReencodeMode));
    }

    partial void OnIsRemoveAllowedChanged(bool value)
    {
        if (!value && SelectedAudioMode == AudioMode.Remove)
            SelectedAudioMode = AudioMode.Copy;
    }
}
