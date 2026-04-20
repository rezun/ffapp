using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;
using FFApp.Core.Validation;

namespace FFApp.ViewModels.Quality;

public sealed partial class Av1QualitySettingsViewModel : CodecQualitySettingsViewModel
{
    public override VideoCodec Codec => VideoCodec.Av1;

    public IReadOnlyList<int> AvailableTunes { get; } = CompatibilityData.Av1TuneOptions.ToList();
    public IReadOnlyList<int> AvailableScm { get; } = CompatibilityData.Av1ScmOptions.ToList();

    [ObservableProperty]
    private int _crf = 30;

    [ObservableProperty]
    private int _preset = 6;

    [ObservableProperty]
    private int _tune;

    [ObservableProperty]
    private int _enableOverlays = 1;

    [ObservableProperty]
    private int _scm = 2;

    [ObservableProperty]
    private int _enableVarianceBoost;

    [ObservableProperty]
    private string _maxBitrateKbpsText = "";

    [ObservableProperty]
    private string _additionalOptions = "";

    public bool EnableOverlaysChecked
    {
        get => EnableOverlays != 0;
        set => EnableOverlays = value ? 1 : 0;
    }

    partial void OnEnableOverlaysChanged(int value) =>
        OnPropertyChanged(nameof(EnableOverlaysChecked));

    public bool EnableVarianceBoostChecked
    {
        get => EnableVarianceBoost != 0;
        set => EnableVarianceBoost = value ? 1 : 0;
    }

    partial void OnEnableVarianceBoostChanged(int value) =>
        OnPropertyChanged(nameof(EnableVarianceBoostChecked));

    public int MaxBitrateKbps =>
        int.TryParse(MaxBitrateKbpsText, out var v) && v > 0 ? v : 0;

    public override void Reset()
    {
        Crf = 30;
        Preset = 6;
        Tune = 0;
        EnableOverlays = 1;
        Scm = 2;
        EnableVarianceBoost = 0;
        MaxBitrateKbpsText = "";
        AdditionalOptions = "";
    }

    public override QualityOptions BuildQualityOptions() =>
        new Av1QualityOptions(Crf, Preset, Tune, EnableOverlays, Scm, MaxBitrateKbps, EnableVarianceBoost);

    public override VideoEncoderOptions BuildEncoderOptions() =>
        new Av1EncoderOptions(Preset, Tune, EnableOverlays, Scm, EnableVarianceBoost, AdditionalOptions);

    public override void ApplyQuality(QualityOptions options)
    {
        if (options is not Av1QualityOptions av1)
            return;
        Crf = av1.Crf;
        Preset = av1.Preset;
        Tune = av1.Tune;
        EnableOverlays = av1.EnableOverlays;
        Scm = av1.Scm;
        EnableVarianceBoost = av1.EnableVarianceBoost;
        MaxBitrateKbpsText = av1.MaxBitrateKbps > 0 ? av1.MaxBitrateKbps.ToString() : "";
    }

    public override void ApplyEncoder(VideoEncoderOptions options)
    {
        if (options is not Av1EncoderOptions av1)
            return;
        Preset = av1.Preset;
        Tune = av1.Tune;
        EnableOverlays = av1.EnableOverlays;
        Scm = av1.Scm;
        EnableVarianceBoost = av1.EnableVarianceBoost;
        AdditionalOptions = av1.AdditionalOptions;
    }
}
