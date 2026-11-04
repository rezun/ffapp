using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;

namespace FFApp.ViewModels;

public sealed partial class SubtitleTrackViewModel : ObservableObject
{
    public int StreamIndex { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    public SubtitleTrackViewModel(SubtitleStreamInfo stream, int trackNumber, bool isSelected)
    {
        StreamIndex = stream.Index;
        var parts = new List<string>
        {
            $"Track {trackNumber}",
            stream.Language ?? "Unknown language",
            stream.CodecName ?? "Unknown codec",
        };
        if (!string.IsNullOrWhiteSpace(stream.Details.Title))
            parts.Add(stream.Details.Title);
        if (stream.Details.Disposition.Forced)
            parts.Add("forced");
        if (stream.Details.Disposition.HearingImpaired)
            parts.Add("SDH");
        Label = string.Join(" · ", parts);
        _isSelected = isSelected;
    }
}
