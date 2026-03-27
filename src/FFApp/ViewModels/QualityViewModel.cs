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
    private bool _isH26xCodec = true;

    public IReadOnlyList<string> AvailablePresets { get; private set; } =
        CompatibilityData.H26xPresetOptions;

    public IReadOnlyList<string> AvailableProfiles { get; private set; } =
        CompatibilityData.H264ProfileOptions;

    public void UpdateForCodec(VideoCodec codec)
    {
        switch (codec)
        {
            case VideoCodec.H264:
                IsH26xCodec = true;
                AvailablePresets = CompatibilityData.QualityPresetOptions(codec);
                AvailableProfiles = CompatibilityData.QualityProfileOptions(codec);
                OnPropertyChanged(nameof(AvailablePresets));
                OnPropertyChanged(nameof(AvailableProfiles));
                Crf = 23;
                H26xPreset = "medium";
                H26xProfile = "high";
                break;

            case VideoCodec.H265:
                IsH26xCodec = true;
                AvailablePresets = CompatibilityData.QualityPresetOptions(codec);
                AvailableProfiles = CompatibilityData.QualityProfileOptions(codec);
                OnPropertyChanged(nameof(AvailablePresets));
                OnPropertyChanged(nameof(AvailableProfiles));
                Crf = 28;
                H26xPreset = "medium";
                H26xProfile = "main";
                break;

            case VideoCodec.Vp8:
                IsH26xCodec = false;
                VpxQuality = 31;
                VpxCpuUsed = 4;
                VpxTileColumns = 0;
                VpxTileRows = 0;
                break;

            case VideoCodec.Vp9:
                IsH26xCodec = false;
                VpxQuality = 32;
                VpxCpuUsed = 2;
                VpxTileColumns = 0;
                VpxTileRows = 0;
                break;
        }
    }
}
