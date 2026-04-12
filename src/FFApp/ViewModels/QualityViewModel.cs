using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;
using FFApp.Core.Validation;

namespace FFApp.ViewModels;

public partial class QualityViewModel : ObservableObject
{
    [ObservableProperty]
    private int _crf = 23;

    [ObservableProperty]
    private string _h26xPreset = "medium";

    [ObservableProperty]
    private string _h26xProfile = "high";

    [ObservableProperty]
    private int _vpxQuality = 31;

    [ObservableProperty]
    private bool _vpxTwoPass;

    [ObservableProperty]
    private int _vpxCpuUsed = 2;

    [ObservableProperty]
    private int _vpxTileColumns;

    [ObservableProperty]
    private int _vpxTileRows;

    [ObservableProperty]
    private int _av1Crf = 30;

    [ObservableProperty]
    private int _av1Preset = 6;

    [ObservableProperty]
    private int _av1Tune;

    [ObservableProperty]
    private int _av1EnableOverlays = 1;

    [ObservableProperty]
    private int _av1Scm = 2;

    [ObservableProperty]
    private bool _isH26xCodec = true;

    [ObservableProperty]
    private bool _isVpxCodec;

    [ObservableProperty]
    private bool _isAv1Codec;

    public IReadOnlyList<string> AvailablePresets { get; private set; } =
        CompatibilityData.H26xPresetOptions;

    public IReadOnlyList<string> AvailableProfiles { get; private set; } =
        CompatibilityData.H264ProfileOptions;

    public IReadOnlyList<int> AvailableTileColumns { get; } = [0, 1, 2, 3, 4, 5, 6];

    public IReadOnlyList<int> AvailableTileRows { get; } = [0, 1, 2];

    public IReadOnlyList<int> AvailableAv1Tunes { get; } =
        CompatibilityData.Av1TuneOptions.ToList();

    public IReadOnlyList<int> AvailableAv1EnableOverlays { get; } =
        CompatibilityData.Av1EnableOverlaysOptions.ToList();

    public IReadOnlyList<int> AvailableAv1Scm { get; } =
        CompatibilityData.Av1ScmOptions.ToList();

    public void UpdateForCodec(VideoCodec codec)
    {
        IsH26xCodec = codec is VideoCodec.H264 or VideoCodec.H265;
        IsVpxCodec = codec is VideoCodec.Vp8 or VideoCodec.Vp9;
        IsAv1Codec = codec is VideoCodec.Av1;

        switch (codec)
        {
            case VideoCodec.H264:
                AvailablePresets = CompatibilityData.QualityPresetOptions(codec);
                AvailableProfiles = CompatibilityData.QualityProfileOptions(codec);
                OnPropertyChanged(nameof(AvailablePresets));
                OnPropertyChanged(nameof(AvailableProfiles));
                Crf = 23;
                H26xPreset = "medium";
                H26xProfile = "high";
                break;

            case VideoCodec.H265:
                AvailablePresets = CompatibilityData.QualityPresetOptions(codec);
                AvailableProfiles = CompatibilityData.QualityProfileOptions(codec);
                OnPropertyChanged(nameof(AvailablePresets));
                OnPropertyChanged(nameof(AvailableProfiles));
                Crf = 28;
                H26xPreset = "medium";
                H26xProfile = "main";
                break;

            case VideoCodec.Vp8:
                VpxQuality = 31;
                VpxCpuUsed = 4;
                VpxTileColumns = 0;
                VpxTileRows = 0;
                break;

            case VideoCodec.Vp9:
                VpxQuality = 32;
                VpxCpuUsed = 2;
                VpxTileColumns = 0;
                VpxTileRows = 0;
                break;

            case VideoCodec.Av1:
                Av1Crf = 30;
                Av1Preset = 6;
                Av1Tune = 0;
                Av1EnableOverlays = 1;
                Av1Scm = 2;
                break;
        }
    }
}
