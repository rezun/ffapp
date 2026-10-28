using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FFRux.Core;
using FFRux.Core.Models;

namespace FFRux.ViewModels;

/// <summary>
/// The properties pane. With several tracks selected it edits them together: fields whose values
/// differ show "Multiple values" and change only when the user edits them.
/// </summary>
public sealed partial class TrackPropertiesViewModel : ObservableObject
{
    public const string MultipleValues = "Multiple values";

    private IReadOnlyList<TrackViewModel> _tracks = [];
    private OutputFormatInfo _format = OutputFormats.Get(OutputFormat.Mkv);
    private bool _loading;

    public IReadOnlyList<Language> Languages { get; } = Core.Models.Languages.All;
    public IReadOnlyList<TextEncoding> Encodings { get; } = TextEncodings.All;

    public IReadOnlyList<TrackViewModel> Tracks => _tracks;
    public bool HasSelection => _tracks.Count > 0;
    public bool IsSingle => _tracks.Count == 1;
    public bool IsMultiple => _tracks.Count > 1;
    public TrackViewModel? Single => _tracks.Count == 1 ? _tracks[0] : null;

    [ObservableProperty] private bool _isEditable = true;
    [ObservableProperty] private string _headerText = "";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _nameWatermark = "";
    [ObservableProperty] private bool _nameEnabled = true;
    [ObservableProperty] private string? _nameHint;

    [ObservableProperty] private Language? _language;
    [ObservableProperty] private string _languageWatermark = "";
    [ObservableProperty] private bool _languageEnabled = true;
    [ObservableProperty] private string? _languageHint;

    [ObservableProperty] private decimal? _timeshiftMs;
    [ObservableProperty] private string _timeshiftWatermark = "";
    [ObservableProperty] private bool _timeshiftEnabled;
    [ObservableProperty] private string? _timeshiftHint;

    [ObservableProperty] private bool _showEncoding;
    [ObservableProperty] private TextEncoding? _encoding;
    [ObservableProperty] private string? _encodingHint;

    [ObservableProperty] private bool? _isDefault;
    [ObservableProperty] private bool? _isForced;
    [ObservableProperty] private bool? _isHearingImpaired;
    [ObservableProperty] private bool? _isVisualImpaired;
    [ObservableProperty] private bool? _isCommentary;
    [ObservableProperty] private bool? _isOriginal;
    [ObservableProperty] private bool _defaultSupported = true;
    [ObservableProperty] private bool _forcedSupported = true;
    [ObservableProperty] private bool _hearingImpairedSupported = true;
    [ObservableProperty] private bool _visualImpairedSupported = true;
    [ObservableProperty] private bool _commentarySupported = true;
    [ObservableProperty] private bool _originalSupported = true;
    [ObservableProperty] private string _flagsHint = "";

    [ObservableProperty] private string _statusSymbol = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private CompatibilityLevel? _statusLevel;

    [ObservableProperty] private string _sourceFile = "";
    [ObservableProperty] private string _sourceCodec = "";
    [ObservableProperty] private string _sourceStream = "";
    [ObservableProperty] private string? _sourcePrefill;

    public bool IsStatusCopy => StatusLevel == CompatibilityLevel.Copy;
    public bool IsStatusConvert => StatusLevel == CompatibilityLevel.Convert;
    public bool IsStatusWarning => StatusLevel == CompatibilityLevel.Warning;
    public bool IsStatusError => StatusLevel == CompatibilityLevel.Incompatible;

    /// <summary>Loads the fields for a new selection. Editing fields are only reloaded here, never while typing.</summary>
    public void Select(IReadOnlyList<TrackViewModel> tracks, OutputFormatInfo format)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(format);
        _tracks = tracks;
        _format = format;
        OnPropertyChanged(nameof(Tracks));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSingle));
        OnPropertyChanged(nameof(IsMultiple));
        OnPropertyChanged(nameof(Single));

        _loading = true;
        try
        {
            LoadFields();
        }
        finally
        {
            _loading = false;
        }
        Refresh(format);
    }

    /// <summary>Updates what depends on the format and the compatibility check, keeping the edit fields as they are.</summary>
    public void Refresh(OutputFormatInfo format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _format = format;
        if (_tracks.Count == 0)
            return;

        NameEnabled = format.TrackNames != TrackNameStorage.None;
        NameHint = NameEnabled ? null : $"{format.Name} stores no track names. The title above names the file.";
        LanguageEnabled = format.SupportsLanguage;
        LanguageHint = LanguageEnabled ? null : $"{format.Name} stores no track language.";

        DefaultSupported = format.SupportedFlags.HasFlag(TrackFlags.Default);
        ForcedSupported = format.SupportedFlags.HasFlag(TrackFlags.Forced);
        HearingImpairedSupported = format.SupportedFlags.HasFlag(TrackFlags.HearingImpaired);
        VisualImpairedSupported = format.SupportedFlags.HasFlag(TrackFlags.VisualImpaired);
        CommentarySupported = format.SupportedFlags.HasFlag(TrackFlags.Commentary);
        OriginalSupported = format.SupportedFlags.HasFlag(TrackFlags.Original);
        FlagsHint = format.SupportedFlags switch
        {
            TrackFlags.None => $"{format.Name} stores no track flags.",
            _ when format.SupportedFlags == TrackFlagNames.All => $"{format.Name} stores all flags.",
            _ => $"Greyed flags can't be stored in {format.ShortName}.",
        };

        if (Single is { } track)
        {
            StatusLevel = track.StatusLevel;
            StatusSymbol = track.StatusSymbol;
            StatusText = track.StatusText;
            StatusDetail = track.StatusDetail;
        }
        else
        {
            var levels = _tracks.Where(t => t.Enabled).Select(t => t.StatusLevel).OfType<CompatibilityLevel>().ToList();
            StatusLevel = levels.Count == 0 ? null : levels.Max();
            StatusSymbol = "";
            StatusText = string.Create(CultureInfo.InvariantCulture, $"{_tracks.Count} tracks selected");
            StatusDetail = StatusLevel switch
            {
                CompatibilityLevel.Incompatible => "At least one of them can't be stored in this format.",
                CompatibilityLevel.Warning => "At least one of them has a warning.",
                CompatibilityLevel.Convert => "At least one of them is converted.",
                CompatibilityLevel.Copy => "All of them are copied as they are.",
                _ => "None of them is included.",
            };
        }
        OnPropertyChanged(nameof(IsStatusCopy));
        OnPropertyChanged(nameof(IsStatusConvert));
        OnPropertyChanged(nameof(IsStatusWarning));
        OnPropertyChanged(nameof(IsStatusError));
    }

    private void LoadFields()
    {
        if (_tracks.Count == 0)
            return;

        var first = _tracks[0];
        HeaderText = Single is { } single
            ? $"{CompatibilityChecker.KindLabel(single.Kind)} · {single.CodecLabel}"
            : string.Create(CultureInfo.InvariantCulture, $"{_tracks.Count} tracks");

        var sameName = _tracks.All(t => (t.Name ?? "") == (first.Name ?? ""));
        Name = sameName ? first.Name ?? "" : "";
        NameWatermark = sameName ? "" : MultipleValues;

        var sameLanguage = _tracks.All(t => t.Language == first.Language);
        Language = sameLanguage ? first.Language : null;
        LanguageWatermark = sameLanguage ? "" : MultipleValues;

        TimeshiftEnabled = _tracks.All(t => t.CanShift);
        var sameShift = _tracks.All(t => t.TimeshiftMs == first.TimeshiftMs);
        TimeshiftMs = TimeshiftEnabled && sameShift ? first.TimeshiftMs : null;
        TimeshiftWatermark = TimeshiftEnabled && !sameShift ? MultipleValues : "";
        TimeshiftHint = TimeshiftEnabled
            ? "Positive values play the track later, negative values earlier."
            : "Video tracks are the reference and can't be shifted.";

        ShowEncoding = _tracks.All(t => t.SourceFile.IsStandaloneTextSubtitle);
        if (ShowEncoding)
        {
            var sameEncoding = _tracks.All(t => t.SourceFile.Encoding == first.SourceFile.Encoding);
            Encoding = sameEncoding ? first.SourceFile.Encoding : null;
            EncodingHint = _tracks.All(t => t.SourceFile.IsUnicodeText)
                ? "The file is Unicode, so no conversion is needed."
                : "The file is not UTF-8. Choose its encoding if special characters look wrong.";
        }

        IsDefault = Common(TrackFlags.Default);
        IsForced = Common(TrackFlags.Forced);
        IsHearingImpaired = Common(TrackFlags.HearingImpaired);
        IsVisualImpaired = Common(TrackFlags.VisualImpaired);
        IsCommentary = Common(TrackFlags.Commentary);
        IsOriginal = Common(TrackFlags.Original);

        SourceFile = Single?.SourceFile.FileName ?? string.Join(", ", _tracks.Select(t => t.SourceFile.FileName).Distinct());
        SourceCodec = string.Join(", ", _tracks.Select(t => t.CodecName).Distinct());
        SourceStream = Single is { } one
            ? string.Create(CultureInfo.InvariantCulture, $"Track {one.Key.StreamIndex} of source {one.SourceNumber}")
            : MultipleValues;
        SourcePrefill = Single?.PrefillText;
    }

    private bool? Common(TrackFlags flag)
    {
        var first = _tracks[0].HasFlag(flag);
        return _tracks.All(t => t.HasFlag(flag) == first) ? first : null;
    }

    partial void OnNameChanged(string value)
    {
        if (_loading) return;
        NameWatermark = "";
        foreach (var track in _tracks)
            track.Name = value;
    }

    partial void OnLanguageChanged(Language? value)
    {
        // The picker clears its selection while the user types; only a chosen language is applied.
        if (_loading || value is null) return;
        LanguageWatermark = "";
        foreach (var track in _tracks)
            track.Language = value;
    }

    partial void OnTimeshiftMsChanged(decimal? value)
    {
        if (_loading || value is null || !TimeshiftEnabled) return;
        TimeshiftWatermark = "";
        var milliseconds = (int)Math.Round(value.Value);
        foreach (var track in _tracks)
            track.TimeshiftMs = milliseconds;
    }

    partial void OnEncodingChanged(TextEncoding? value)
    {
        if (_loading || value is null) return;
        foreach (var source in _tracks.Select(t => t.SourceFile).Distinct())
            source.Encoding = value;
    }

    partial void OnIsDefaultChanged(bool? value) => ApplyFlag(TrackFlags.Default, value);
    partial void OnIsForcedChanged(bool? value) => ApplyFlag(TrackFlags.Forced, value);
    partial void OnIsHearingImpairedChanged(bool? value) => ApplyFlag(TrackFlags.HearingImpaired, value);
    partial void OnIsVisualImpairedChanged(bool? value) => ApplyFlag(TrackFlags.VisualImpaired, value);
    partial void OnIsCommentaryChanged(bool? value) => ApplyFlag(TrackFlags.Commentary, value);
    partial void OnIsOriginalChanged(bool? value) => ApplyFlag(TrackFlags.Original, value);

    private void ApplyFlag(TrackFlags flag, bool? value)
    {
        if (_loading || value is null) return;
        foreach (var track in _tracks)
            track.SetFlag(flag, value.Value);
    }
}
