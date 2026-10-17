using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;
using FFApp.Core.Validation;

namespace FFApp.ViewModels.Quality;

public sealed partial class Av1QualitySettingsViewModel : CodecQualitySettingsViewModel
{
    public override VideoCodec Codec => VideoCodec.Av1;

    public IReadOnlyList<int> AvailableTunes { get; } = CompatibilityData.Av1TuneOptions.ToList();
    public IReadOnlyList<int> AvailableScm { get; } = CompatibilityData.Av1ScmOptions.ToList();
    public IReadOnlyList<int> AvailableFilmGrainStrengths { get; } = Enumerable.Range(
        CompatibilityData.Av1FilmGrainMin + 1,
        CompatibilityData.Av1FilmGrainMax - CompatibilityData.Av1FilmGrainMin).ToList();

    public IReadOnlyList<int> AvailableVarianceBoostStrengths { get; } = CompatibilityData.Av1VarianceBoostStrengthOptions;
    public IReadOnlyList<int> AvailableVarianceOctiles { get; } = CompatibilityData.Av1VarianceOctileOptions;

    [ObservableProperty]
    private int _varianceBoostStrength = 2;

    [ObservableProperty]
    private int _varianceOctile = 5;

    private int _lastFilmGrainStrength = CompatibilityData.Av1FilmGrainDefaultStrength;

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
    [NotifyPropertyChangedFor(nameof(IsFilmGrainEnabled))]
    private int _filmGrain;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilmGrainDenoiseChecked))]
    private int _filmGrainDenoise;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AdaptiveFilmGrainChecked))]
    private int _adaptiveFilmGrain = 1;

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

    public bool IsFilmGrainEnabled
    {
        get => FilmGrain > 0;
        set
        {
            if (value == IsFilmGrainEnabled)
                return;
            FilmGrain = value ? _lastFilmGrainStrength : 0;
        }
    }

    partial void OnFilmGrainChanged(int value)
    {
        if (value > 0)
            _lastFilmGrainStrength = value;
    }

    public bool FilmGrainDenoiseChecked
    {
        get => FilmGrainDenoise != 0;
        set => FilmGrainDenoise = value ? 1 : 0;
    }

    public bool AdaptiveFilmGrainChecked
    {
        get => AdaptiveFilmGrain != 0;
        set => AdaptiveFilmGrain = value ? 1 : 0;
    }

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
        VarianceBoostStrength = 2;
        VarianceOctile = 5;
        _lastFilmGrainStrength = CompatibilityData.Av1FilmGrainDefaultStrength;
        FilmGrain = 0;
        FilmGrainDenoise = 0;
        AdaptiveFilmGrain = 1;
        MaxBitrateKbpsText = "";
        AdditionalOptions = "";
    }

    public override QualityOptions BuildQualityOptions() =>
        new Av1QualityOptions(Crf, Preset, Tune, EnableOverlays, Scm, MaxBitrateKbps,
            EnableVarianceBoost, FilmGrain, FilmGrainDenoise, AdaptiveFilmGrain, VarianceBoostStrength, VarianceOctile);

    public override VideoEncoderOptions BuildEncoderOptions() =>
        new Av1EncoderOptions(Preset, Tune, EnableOverlays, Scm, EnableVarianceBoost,
            AdditionalOptions, FilmGrain, FilmGrainDenoise, AdaptiveFilmGrain, VarianceBoostStrength, VarianceOctile);

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
        VarianceBoostStrength = av1.VarianceBoostStrength;
        VarianceOctile = av1.VarianceOctile;
        FilmGrain = av1.FilmGrain;
        FilmGrainDenoise = av1.FilmGrainDenoise;
        AdaptiveFilmGrain = av1.AdaptiveFilmGrain;
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
        VarianceBoostStrength = av1.VarianceBoostStrength;
        VarianceOctile = av1.VarianceOctile;
        FilmGrain = av1.FilmGrain;
        FilmGrainDenoise = av1.FilmGrainDenoise;
        AdaptiveFilmGrain = av1.AdaptiveFilmGrain;
        AdditionalOptions = av1.AdditionalOptions;
    }
}
