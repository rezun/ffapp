using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Validation;

namespace FFApp.ViewModels;

public sealed partial class ColorMetadataViewModel : ObservableObject
{
    private VideoStreamInfo? _source;
    private HdrMode _hdrMode = HdrMode.Preserve;
    private bool _isReencode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomVisible), nameof(OutputSummary), nameof(CompactSummary))]
    private ColorMetadataMode _selectedMode = ColorMetadataMode.Automatic;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputSummary), nameof(CompactSummary))]
    private string _selectedPrimaries = "bt709";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputSummary), nameof(CompactSummary))]
    private string _selectedTransfer = "bt709";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputSummary), nameof(CompactSummary))]
    private string _selectedMatrix = "bt709";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputSummary), nameof(CompactSummary))]
    private string _selectedRange = "tv";

    [ObservableProperty]
    private bool _areDetailsVisible;

    public string CompactSummary
    {
        get
        {
            if (_source is null)
                return "";
            if (!_isReencode)
                return "Unchanged";
            if (ColorMetadataResolver.HasHdrSignal(_source))
                return _source.IsHdr ? "From HDR settings" : "Incomplete HDR tags";
            var output = ColorMetadataResolver.Resolve(_source, BuildSettings(), _hdrMode);
            var assumed = SelectedMode == ColorMetadataMode.AssumeBt709
                && output != ColorMetadataResolver.Source(_source);
            if (assumed)
                return "Missing values assumed";
            if (output.Primaries is null && output.Transfer is null && output.Matrix is null && output.Range is null)
                return "Unspecified";
            if (output.Primaries is null || output.Transfer is null || output.Matrix is null || output.Range is null)
                return "Incomplete tags";
            var primaries = output.Primaries == "bt709" ? "BT.709" : output.Primaries;
            return $"{primaries} · {(output.Range == "pc" ? "Full" : "Limited")}";
        }
    }

    public IReadOnlyList<ColorMetadataMode> AvailableModes { get; } = Enum.GetValues<ColorMetadataMode>();
    public IReadOnlyList<string> AvailablePrimaries => CompatibilityData.ColorPrimariesOptions;
    public IReadOnlyList<string> AvailableTransfers => CompatibilityData.ColorTransferOptions;
    public IReadOnlyList<string> AvailableMatrices => CompatibilityData.ColorMatrixOptions;
    public IReadOnlyList<string> AvailableRanges => CompatibilityData.ColorRangeOptions;
    public bool IsEditable => _isReencode && !ColorMetadataResolver.HasHdrSignal(_source);
    public bool IsCustomVisible => IsEditable && SelectedMode == ColorMetadataMode.Custom;
    public string SourceSummary => _source is null ? "Source: no video loaded." :
        $"Source: {Describe(ColorMetadataResolver.Source(_source))}";

    public string OutputSummary
    {
        get
        {
            if (_source is null)
                return "Output: load a video to inspect its color information.";
            if (!_isReencode)
                return "Color information is unchanged when copying video.";
            var output = ColorMetadataResolver.Resolve(_source, BuildSettings(), _hdrMode);
            var description = $"Output: {Describe(output)}";
            if (ColorMetadataResolver.HasHdrSignal(_source))
                return description + (_source.IsHdr
                    ? " — determined by HDR settings."
                    : " — incomplete HDR information; SDR overrides are disabled.");
            if (SelectedMode == ColorMetadataMode.AssumeBt709
                && output != ColorMetadataResolver.Source(_source))
                return description + " — missing values assumed as BT.709 / Limited.";
            if (SelectedMode == ColorMetadataMode.Custom)
                return description + " — manual labels; pixels are not color-converted.";
            return description + (output.Primaries is null || output.Transfer is null
                || output.Matrix is null || output.Range is null
                ? " — unspecified values remain unspecified." : " — from source.");
        }
    }

    public void UpdateContext(VideoStreamInfo? source, HdrMode hdrMode, bool isReencode)
    {
        _source = source;
        _hdrMode = hdrMode;
        _isReencode = isReencode;
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsCustomVisible));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(OutputSummary));
        OnPropertyChanged(nameof(CompactSummary));
    }

    public ColorMetadataSettings BuildSettings() => new(
        SelectedMode, SelectedPrimaries, SelectedTransfer, SelectedMatrix, SelectedRange);

    public void ApplySettings(ColorMetadataSettings settings)
    {
        SelectedMode = settings.Mode;
        SelectedPrimaries = settings.Primaries;
        SelectedTransfer = settings.Transfer;
        SelectedMatrix = settings.Matrix;
        SelectedRange = settings.Range;
    }

    private static string Describe(ColorMetadata metadata) =>
        $"primaries {metadata.Primaries ?? "unspecified"}, transfer {metadata.Transfer ?? "unspecified"}, " +
        $"matrix {metadata.Matrix ?? "unspecified"}, range {metadata.Range switch
        {
            "tv" => "Limited", "pc" => "Full", _ => "unspecified",
        }}";
}
