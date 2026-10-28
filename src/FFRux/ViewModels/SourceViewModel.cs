using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;
using FFRux.Core;
using FFRux.Core.Models;

namespace FFRux.ViewModels;

/// <summary>One added file in the source list, with its numbered marker.</summary>
public sealed partial class SourceViewModel : ObservableObject
{
    private SourceViewModel(string path, SourceMediaInfo? info, string? error)
    {
        Path = path;
        MediaInfo = info;
        Error = error;
    }

    public event EventHandler? Changed;

    public string Path { get; }
    public SourceMediaInfo? MediaInfo { get; }
    public string? Error { get; }
    public bool IsError => Error is not null;
    public string FileName => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkerIndex))]
    private int _number;

    /// <summary>Picks one of the marker colors; the number keeps the marker readable without color.</summary>
    public int MarkerIndex => (Number - 1) % 6;

    /// <summary>Set for standalone text subtitle files, whose character set FFmpeg cannot detect.</summary>
    public bool IsStandaloneTextSubtitle { get; init; }

    /// <summary>True when the subtitle file is UTF-8 or UTF-16, so no conversion is needed.</summary>
    public bool IsUnicodeText { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private TextEncoding _encoding = TextEncodings.Utf8;

    public FileNameHint Hint { get; init; } = FileNameHint.None;

    public IReadOnlyList<ChapterInfo> Chapters => MediaInfo?.Chapters ?? [];

    public string Summary => Error ?? Describe();

    public static SourceViewModel Failed(string path, string error) => new(path, null, error);

    public static SourceViewModel Probed(string path, SourceMediaInfo info, bool isStandaloneText, bool isUnicodeText) =>
        new(path, info, null)
        {
            IsStandaloneTextSubtitle = isStandaloneText,
            IsUnicodeText = isUnicodeText,
            Encoding = isStandaloneText && !isUnicodeText ? TextEncodings.Fallback : TextEncodings.Utf8,
            Hint = FileNameHints.Parse(path),
        };

    public RemuxSource? ToModel() => MediaInfo is null
        ? null
        : new RemuxSource
        {
            Number = Number,
            Path = Path,
            MediaInfo = MediaInfo,
            SubtitleCharset = IsStandaloneTextSubtitle && Encoding != TextEncodings.Utf8 ? Encoding.Charset : null,
        };

    partial void OnEncodingChanged(TextEncoding value) => Changed?.Invoke(this, EventArgs.Empty);

    private string Describe()
    {
        if (MediaInfo is not { } info)
            return "";

        var parts = new List<string> { ContainerLabel(info) };
        if (IsStandaloneTextSubtitle)
            parts.Add(Encoding.Charset == TextEncodings.Utf8.Charset ? "UTF-8" : Encoding.Label.Split(' ')[0]);
        if (info.DurationSeconds is { } seconds and > 0 && !IsStandaloneTextSubtitle)
            parts.Add(FormatDuration(seconds));
        if (info.SizeBytes is { } size and > 0)
            parts.Add(SizeEstimator.Format(size));

        string?[] counts =
        [
            Count(info.VideoStreams.Length, "video", "video"),
            Count(info.AudioStreams.Length, "audio", "audio"),
            Count(info.SubtitleStreams.Length, "subtitle", "subtitles"),
        ];
        var streams = string.Join(", ", counts.OfType<string>());
        parts.Add(streams.Length == 0 ? "No tracks FFRux can use" : streams);
        return string.Join(" · ", parts);
    }

    internal static string ContainerLabel(SourceMediaInfo info)
    {
        var single = info.StreamCount == 1;
        if (single && info.AudioStreams.Length == 1 && info.AudioStreams[0].CodecName is { } audio)
            return Codecs.Label(audio, info.AudioStreams[0].Details.Profile);
        if (single && info.SubtitleStreams.Length == 1 && info.SubtitleStreams[0].CodecName is { } subtitle)
            return Codecs.Label(subtitle);

        var extension = System.IO.Path.GetExtension(info.SourcePath);
        var format = info.ContainerFormat?.Split(',')[0];
        return format switch
        {
            "matroska" when extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) => "WebM",
            "matroska" => "Matroska",
            "mov" when extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) => "QuickTime",
            "mov" => "MP4",
            "mpegts" => "MPEG-TS",
            "ogg" => "Ogg",
            "avi" => "AVI",
            null => "Unknown format",
            _ => format.ToUpperInvariant(),
        };
    }

    internal static string FormatDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Round(seconds));
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string? Count(int count, string singular, string plural) =>
        count switch
        {
            0 => null,
            1 => string.Concat("1 ", singular),
            _ => string.Create(CultureInfo.InvariantCulture, $"{count} {plural}"),
        };
}
