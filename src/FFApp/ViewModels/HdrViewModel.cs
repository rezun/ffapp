using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;
using FFApp.Core.Validation;

namespace FFApp.ViewModels;

public partial class HdrViewModel : ObservableObject
{
    [ObservableProperty]
    private HdrMode _selectedHdrMode = HdrMode.Preserve;

    [ObservableProperty]
    private ToneMapEngine _selectedEngine = ToneMapEngine.Standard;

    [ObservableProperty]
    private ToneMapAlgorithm _selectedAlgorithm = ToneMapAlgorithm.Hable;

    [ObservableProperty]
    private double _desaturation = 2.0;

    [ObservableProperty]
    private int? _peakNits;

    [ObservableProperty]
    private string _peakNitsText = "";

    [ObservableProperty]
    private LibplaceboToneMapAlgorithm _selectedLibplaceboAlgorithm = LibplaceboToneMapAlgorithm.Auto;

    [ObservableProperty]
    private GamutMode _selectedGamutMode = GamutMode.Perceptual;

    [ObservableProperty]
    private double _saturation = 1.0;

    [ObservableProperty]
    private double _contrastRecovery = 0.3;

    [ObservableProperty]
    private bool _peakDetect = true;

    [ObservableProperty]
    private double? _tonemappingParam;

    [ObservableProperty]
    private string _tonemappingParamText = "";

    [ObservableProperty]
    private bool _isSourceHdr;

    [ObservableProperty]
    private HdrFormat _sourceHdrFormat = HdrFormat.Sdr;

    [ObservableProperty]
    private bool _isHdrModeVisible;

    [ObservableProperty]
    private bool _isToneMapSettingsVisible;

    [ObservableProperty]
    private bool _isPreserveAllowed = true;

    [ObservableProperty]
    private bool _isLibplaceboAvailable;

    [ObservableProperty]
    private bool _isStandardEngineVisible = true;

    [ObservableProperty]
    private bool _isLibplaceboEngineVisible;

    [ObservableProperty]
    private string? _hdrModeNote;

    public IReadOnlyList<ToneMapAlgorithm> AvailableAlgorithms { get; } =
        Enum.GetValues<ToneMapAlgorithm>();

    public IReadOnlyList<ToneMapEngine> AvailableEngines { get; } =
        Enum.GetValues<ToneMapEngine>();

    public IReadOnlyList<LibplaceboToneMapAlgorithm> AvailableLibplaceboAlgorithms { get; } =
        Enum.GetValues<LibplaceboToneMapAlgorithm>();

    public IReadOnlyList<GamutMode> AvailableGamutModes { get; } =
        Enum.GetValues<GamutMode>();

    partial void OnPeakNitsTextChanged(string value)
    {
        PeakNits = int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    }

    partial void OnTonemappingParamTextChanged(string value)
    {
        TonemappingParam = double.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    }

    partial void OnSelectedEngineChanged(ToneMapEngine value)
    {
        UpdateEngineVisibility();
    }

    partial void OnSelectedHdrModeChanged(HdrMode value)
    {
        IsToneMapSettingsVisible = value == HdrMode.TonemapToSdr && IsSourceHdr;
    }

    partial void OnIsLibplaceboAvailableChanged(bool value)
    {
        if (!value && SelectedEngine == ToneMapEngine.Libplacebo)
            SelectedEngine = ToneMapEngine.Standard;

        UpdateEngineVisibility();
    }

    public void UpdateForSource(HdrFormat sourceFormat)
    {
        SourceHdrFormat = sourceFormat;
        IsSourceHdr = sourceFormat != HdrFormat.Sdr;

        if (!IsSourceHdr)
        {
            SelectedHdrMode = HdrMode.Preserve;
            IsPreserveAllowed = true;
            HdrModeNote = null;
        }

        UpdateVisibility();
    }

    public void UpdateForCodec(VideoCodec codec)
    {
        var canPreserve = CompatibilityData.SupportsHdrOutput(codec);
        IsPreserveAllowed = !IsSourceHdr || canPreserve;

        if (IsSourceHdr && !canPreserve)
        {
            SelectedHdrMode = HdrMode.TonemapToSdr;
            HdrModeNote = $"{CompatibilityData.VideoCodecLabel(codec)} does not support HDR output.";
        }
        else
        {
            HdrModeNote = null;
        }

        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        IsToneMapSettingsVisible = IsSourceHdr && SelectedHdrMode == HdrMode.TonemapToSdr;
    }

    private void UpdateEngineVisibility()
    {
        IsStandardEngineVisible = SelectedEngine == ToneMapEngine.Standard || !IsLibplaceboAvailable;
        IsLibplaceboEngineVisible = SelectedEngine == ToneMapEngine.Libplacebo && IsLibplaceboAvailable;
    }
}
