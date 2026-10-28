using CommunityToolkit.Mvvm.ComponentModel;
using FFRux.Core;
using FFRux.Core.Models;

namespace FFRux.ViewModels;

/// <summary>A row in the track table: the track from the file plus what the user changed.</summary>
public sealed partial class TrackViewModel : ObservableObject
{
    public TrackViewModel(SourceViewModel source, SourceTrack track, TrackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(settings);
        SourceFile = source;
        Track = track;
        _enabled = settings.Enabled;
        _name = settings.Name;
        _language = settings.Language;
        _flags = settings.Flags;
        _timeshiftMs = track.Kind == TrackKind.Video ? 0 : settings.TimeshiftMs;
        PrefillText = SourceTracks.DescribePrefill(track, source.Hint);
        source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SourceViewModel.Number))
            {
                Track = Track with { Key = Track.Key with { SourceNumber = source.Number } };
                OnPropertyChanged(nameof(SourceNumber));
                OnPropertyChanged(nameof(MarkerIndex));
            }
        };
    }

    /// <summary>Raised when a setting changes that affects compatibility or the command.</summary>
    public event EventHandler? Changed;

    public SourceViewModel SourceFile { get; }
    public SourceTrack Track { get; private set; }
    public TrackKey Key => Track.Key;
    public TrackKind Kind => Track.Kind;
    public int SourceNumber => SourceFile.Number;
    public int MarkerIndex => SourceFile.MarkerIndex;
    public string CodecLabel => Codecs.Label(Track.CodecName, Track.Details.Profile);
    public string CodecName => Track.CodecName;
    public string? PrefillText { get; }
    public bool CanShift => Kind != TrackKind.Video;

    [ObservableProperty] private bool _isEditable = true;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string? _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageCode))]
    private Language _language;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlagChips))]
    private TrackFlags _flags;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsText))]
    private int _timeshiftMs;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private string _statusSymbol = "";
    [ObservableProperty] private CompatibilityLevel? _statusLevel;

    public string LanguageCode => Language.Code;

    public IReadOnlyList<string> FlagChips =>
        [.. TrackFlagNames.Ordered.Where(flag => Flags.HasFlag(flag)).Select(TrackFlagNames.Label)];

    public string DetailsText
    {
        get
        {
            var description = SourceTracks.Describe(Track);
            if (TimeshiftMs == 0)
                return description;
            var shift = SourceTracks.FormatTimeshift(TimeshiftMs);
            return description.Length == 0 ? shift : string.Concat(description, " · ", shift);
        }
    }

    public bool IsStatusCopy => Enabled && StatusLevel == CompatibilityLevel.Copy;
    public bool IsStatusConvert => Enabled && StatusLevel == CompatibilityLevel.Convert;
    public bool IsStatusWarning => Enabled && StatusLevel == CompatibilityLevel.Warning;
    public bool IsStatusError => Enabled && StatusLevel == CompatibilityLevel.Incompatible;

    public TrackSettings ToSettings() => new()
    {
        Enabled = Enabled,
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name,
        Language = Language,
        Flags = Flags,
        TimeshiftMs = CanShift ? TimeshiftMs : 0,
    };

    public RemuxTrack ToModel() => new(Track, ToSettings());

    public bool HasFlag(TrackFlags flag) => Flags.HasFlag(flag);

    public void SetFlag(TrackFlags flag, bool value) => Flags = value ? Flags | flag : Flags & ~flag;

    /// <summary>Shows what the job does with the track; disabled tracks read "Not included".</summary>
    public void ApplyStatus(TrackCompatibility? status)
    {
        if (!Enabled || status is null)
        {
            StatusLevel = null;
            StatusSymbol = "";
            StatusText = "Not included";
            StatusDetail = "This track is not written to the output.";
        }
        else
        {
            StatusLevel = status.Level;
            StatusSymbol = status.Level switch
            {
                CompatibilityLevel.Copy => "✓",
                CompatibilityLevel.Convert => "⇄",
                CompatibilityLevel.Warning => "▲",
                CompatibilityLevel.Incompatible => "✕",
                _ => throw new ArgumentOutOfRangeException(nameof(status), status.Level, null),
            };
            StatusText = status.StatusText;
            StatusDetail = status.Detail;
        }
        OnPropertyChanged(nameof(IsStatusCopy));
        OnPropertyChanged(nameof(IsStatusConvert));
        OnPropertyChanged(nameof(IsStatusWarning));
        OnPropertyChanged(nameof(IsStatusError));
    }

    partial void OnEnabledChanged(bool value) => RaiseChanged();
    partial void OnNameChanged(string? value) => RaiseChanged();
    partial void OnLanguageChanged(Language value) => RaiseChanged();
    partial void OnFlagsChanged(TrackFlags value) => RaiseChanged();
    partial void OnTimeshiftMsChanged(int value) => RaiseChanged();

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>The header row above each track group in the table.</summary>
public sealed partial class TrackGroupViewModel(TrackKind kind) : ObservableObject
{
    public TrackKind Kind { get; } = kind;

    public string Label { get; } = kind switch
    {
        TrackKind.Video => "Video",
        TrackKind.Audio => "Audio",
        TrackKind.Subtitle => "Subtitles",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    [ObservableProperty] private string _countText = "";
}
