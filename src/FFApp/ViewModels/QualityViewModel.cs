using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;
using FFApp.ViewModels.Quality;

namespace FFApp.ViewModels;

public partial class QualityViewModel : ObservableObject
{
    public H26xQualitySettingsViewModel H264 { get; } = new(VideoCodec.H264);
    public H26xQualitySettingsViewModel H265 { get; } = new(VideoCodec.H265);
    public Av1QualitySettingsViewModel Av1 { get; } = new();
    public VpxQualitySettingsViewModel Vpx { get; } = new();

    [ObservableProperty]
    private CodecQualitySettingsViewModel _active;

    public QualityViewModel()
    {
        _active = H264;
        WireChildNotifications();
    }

    public void UpdateForCodec(VideoCodec codec)
    {
        var next = ViewModelFor(codec);
        if (!ReferenceEquals(Active, next))
            Active = next;
        Active.Reset();
    }

    public CodecQualitySettingsViewModel ViewModelFor(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => H264,
        VideoCodec.H265 => H265,
        VideoCodec.Av1 => Av1,
        VideoCodec.Vp9 => Vpx,
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported video codec."),
    };

    private void WireChildNotifications()
    {
        // Bubble child property changes up so the parent OutputSettingsViewModel's
        // OnChildPropertyChanged hook rebuilds settings when any codec-specific
        // control is edited.
        foreach (var child in new CodecQualitySettingsViewModel[] { H264, H265, Av1, Vpx })
            child.PropertyChanged += (_, e) => OnPropertyChanged(e);
    }
}
