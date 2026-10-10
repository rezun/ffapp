using System.Collections.Immutable;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;

namespace FFApp.ViewModels;

public partial class AudioViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _allAudioTracks = true;

    [ObservableProperty]
    private IReadOnlyList<AudioTrackViewModel> _audioTracks = [];

    public bool IsTrackSelectionVisible => SelectedAudioMode != AudioMode.Remove && AudioTracks.Count > 0;
    public bool IsCustomTrackSelection => !AllAudioTracks;
    public ImmutableArray<int>? SelectedAudioStreamIndices => AllAudioTracks
        ? null
        : [.. AudioTracks.Where(t => t.IsSelected).Select(t => t.StreamIndex)];

    public void UpdateTracks(SourceMediaInfo mediaInfo, ImmutableArray<int>? selectedIndices = null)
    {
        foreach (var track in AudioTracks)
            track.PropertyChanged -= OnTrackPropertyChanged;
        AudioTracks = mediaInfo.AudioStreams.Select((stream, index) =>
            new AudioTrackViewModel(stream, index + 1, selectedIndices is null || selectedIndices.Value.Contains(stream.Index))).ToArray();
        foreach (var track in AudioTracks)
            track.PropertyChanged += OnTrackPropertyChanged;
        AllAudioTracks = selectedIndices is null;
        OnPropertyChanged(nameof(IsTrackSelectionVisible));
        OnPropertyChanged(nameof(SelectedAudioStreamIndices));
    }

    [RelayCommand]
    private void SelectAllTracks() => SetAllTracksSelected(true);

    [RelayCommand]
    private void DeselectAllTracks() => SetAllTracksSelected(false);

    private void SetAllTracksSelected(bool isSelected)
    {
        foreach (var track in AudioTracks)
            track.IsSelected = isSelected;
        AllAudioTracks = false;
    }

    private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioTrackViewModel.IsSelected))
            OnPropertyChanged(nameof(SelectedAudioStreamIndices));
    }

    partial void OnAllAudioTracksChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCustomTrackSelection));
        OnPropertyChanged(nameof(SelectedAudioStreamIndices));
    }

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
    private int _selectedSampleRate;

    [ObservableProperty]
    private IReadOnlyList<int> _availableSampleRateOptions = [0, 8000, 22050, 44100, 48000];

    private static readonly int[] AllSampleRateOptions = [0, 8000, 22050, 44100, 48000, 96000];

    [ObservableProperty]
    private IReadOnlyList<AudioCodec> _availableAudioCodecs = [];

    /// <summary>Default audio bitrate in bits per second (128 kbps).</summary>
    public const int DefaultBitrateBps = 128_000;

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

    private int _maxSourceSampleRate;

    public int MaxSourceSampleRate
    {
        get => _maxSourceSampleRate;
        set
        {
            if (!SetProperty(ref _maxSourceSampleRate, value))
                return;

            AvailableSampleRateOptions = AllSampleRateOptions
                .Where(r => r == 0 || r <= value)
                .ToArray();

            if (!AvailableSampleRateOptions.Contains(SelectedSampleRate))
                SelectedSampleRate = 0;
        }
    }

    [ObservableProperty]
    private bool _isRemoveAllowed = true;

    public bool IsReencodeMode => SelectedAudioMode == AudioMode.Reencode;

    partial void OnSelectedAudioModeChanged(AudioMode value)
    {
        OnPropertyChanged(nameof(IsReencodeMode));
        OnPropertyChanged(nameof(IsTrackSelectionVisible));

        if (value == AudioMode.Reencode
            && SelectedAudioCodec is null
            && AvailableAudioCodecs.Count > 0)
        {
            SelectedAudioCodec = AvailableAudioCodecs[0];
        }
    }

    partial void OnIsRemoveAllowedChanged(bool value)
    {
        if (!value && SelectedAudioMode == AudioMode.Remove)
            SelectedAudioMode = AudioMode.Copy;
    }
}
