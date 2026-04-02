using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;
using FFApp.Core.Validation;

namespace FFApp.ViewModels;

public partial class HdrViewModel : ObservableObject
{
    [ObservableProperty]
    private HdrMode _selectedHdrMode = HdrMode.Preserve;

    [ObservableProperty]
    private ToneMapAlgorithm _selectedAlgorithm = ToneMapAlgorithm.Hable;

    [ObservableProperty]
    private double _desaturation = 2.0;

    [ObservableProperty]
    private int? _peakNits;

    [ObservableProperty]
    private string _peakNitsText = "";

    partial void OnPeakNitsTextChanged(string value)
    {
        PeakNits = int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    }

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
    private string? _hdrModeNote;

    public IReadOnlyList<ToneMapAlgorithm> AvailableAlgorithms { get; } =
        Enum.GetValues<ToneMapAlgorithm>();

    partial void OnSelectedHdrModeChanged(HdrMode value)
    {
        IsToneMapSettingsVisible = value == HdrMode.TonemapToSdr && IsSourceHdr;
    }

    public void UpdateForSource(HdrFormat sourceFormat)
    {
        SourceHdrFormat = sourceFormat;
        IsSourceHdr = sourceFormat != HdrFormat.Sdr;
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
}
