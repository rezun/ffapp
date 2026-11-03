using System.Collections.Immutable;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;
using FFApp.Core.Validation;

namespace FFApp.ViewModels;

public sealed partial class AudioViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _removeTrackTitles;

    public bool IsAudioIncluded => SelectedAudioMode != AudioMode.Remove;

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

    /// <summary>
    /// Channel count options; 0 is "Auto" (keep the source layout). The list is fixed so a
    /// preset's choice stays visible and selected regardless of the loaded source.
    /// </summary>
    public static IReadOnlyList<int> AvailableAudioChannelOptions { get; } = [0, 1, 2, 6];

    [ObservableProperty]
    private int _selectedSampleRate;

    [ObservableProperty]
    private IReadOnlyList<int> _availableSampleRateOptions = [0, .. CompatibilityData.AudioSampleRateOptions[AudioCodec.Aac]];

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

    partial void OnSelectedAudioCodecChanged(AudioCodec? value) => UpdateSampleRateOptions();

    partial void OnSelectedSampleRateChanged(int value)
    {
        // Presets and session restore also assign through this property.
        if (!AvailableSampleRateOptions.Contains(value))
            SelectedSampleRate = 0;
    }

    private void UpdateSampleRateOptions()
    {
        var selectedRate = SelectedSampleRate;
        var codecRates = SelectedAudioCodec is { } codec
            ? CompatibilityData.AudioSampleRateOptions[codec]
            : [];
        // Only the codec limits the list; filtering by the source would drop a preset's rate.
        AvailableSampleRateOptions = [0, .. codecRates];
        SelectedSampleRate = AvailableSampleRateOptions.Contains(selectedRate) ? selectedRate : 0;
    }

    [ObservableProperty]
    private bool _isRemoveAllowed = true;

    public bool IsReencodeMode => SelectedAudioMode == AudioMode.Reencode;

    partial void OnSelectedAudioModeChanged(AudioMode value)
    {
        OnPropertyChanged(nameof(IsAudioIncluded));
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
