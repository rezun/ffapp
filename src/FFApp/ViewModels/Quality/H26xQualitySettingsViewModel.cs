using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;
using FFApp.Core.Validation;

namespace FFApp.ViewModels.Quality;

public sealed partial class H26xQualitySettingsViewModel : CodecQualitySettingsViewModel
{
    public override VideoCodec Codec { get; }

    public IReadOnlyList<string> AvailablePresets { get; }
    public IReadOnlyList<string> AvailableProfiles { get; }
    public string AdditionalOptionsPlaceholder { get; }

    [ObservableProperty]
    private int _crf;

    [ObservableProperty]
    private string _preset = "medium";

    [ObservableProperty]
    private string _profile = "main";

    [ObservableProperty]
    private string _additionalOptions = "";

    public H26xQualitySettingsViewModel(VideoCodec codec)
    {
        if (codec is not (VideoCodec.H264 or VideoCodec.H265))
            throw new ArgumentOutOfRangeException(nameof(codec), codec, "H26x VM requires H.264 or H.265.");

        Codec = codec;
        AvailablePresets = CompatibilityData.QualityPresetOptions(codec);
        AvailableProfiles = CompatibilityData.QualityProfileOptions(codec);
        AdditionalOptionsPlaceholder = codec == VideoCodec.H264
            ? "e.g. ref=4:bframes=3"
            : "e.g. aq-mode=3:psy-rd=1.0";

        Reset();
    }

    public override void Reset()
    {
        var defaults = H26xQualityOptions.ForCodec(Codec);
        Crf = defaults.Crf;
        Preset = defaults.Preset;
        Profile = defaults.Profile;
        AdditionalOptions = "";
    }

    public override QualityOptions BuildQualityOptions() =>
        new H26xQualityOptions(Codec, Crf, Preset, Profile);

    public override VideoEncoderOptions BuildEncoderOptions() =>
        new H26xEncoderOptions(Codec, Preset, Profile, AdditionalOptions);

    public override void ApplyQuality(QualityOptions options)
    {
        if (options is not H26xQualityOptions h26x || h26x.Codec != Codec)
            return;
        Crf = h26x.Crf;
        Preset = h26x.Preset;
        Profile = h26x.Profile;
    }

    public override void ApplyEncoder(VideoEncoderOptions options)
    {
        if (options is not H26xEncoderOptions h26x || h26x.Codec != Codec)
            return;
        Preset = h26x.Preset;
        Profile = h26x.Profile;
        AdditionalOptions = h26x.AdditionalOptions;
    }
}
