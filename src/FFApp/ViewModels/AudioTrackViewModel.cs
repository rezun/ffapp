using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;

namespace FFApp.ViewModels;

public sealed partial class AudioTrackViewModel : ObservableObject
{
    public int StreamIndex { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    public AudioTrackViewModel(AudioStreamInfo stream, int trackNumber, bool isSelected)
    {
        StreamIndex = stream.Index;
        Label = $"Track {trackNumber} · {stream.Language ?? "Unknown language"} · {stream.CodecName ?? "Unknown codec"} · {stream.Channels?.ToString() ?? "?"} channels";
        _isSelected = isSelected;
    }
}
