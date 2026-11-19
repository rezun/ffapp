using FFApp.Core.Models;

namespace FFRux.Core.Models;

public enum TrackKind
{
    Video,
    Audio,
    Subtitle,
}

[Flags]
public enum TrackFlags
{
    None = 0,
    Default = 1,
    Forced = 2,
    HearingImpaired = 4,
    VisualImpaired = 8,
    Commentary = 16,
    Original = 32,
}

/// <summary>Identifies a track across rebuilds: the source it comes from and its FFprobe stream index.</summary>
public readonly record struct TrackKey(int SourceNumber, int StreamIndex);

/// <summary>A track as found in a source file. It never changes; edits live in <see cref="TrackSettings"/>.</summary>
public sealed record SourceTrack
{
    public required TrackKey Key { get; init; }
    public required TrackKind Kind { get; init; }
    public required string CodecName { get; init; }
    public StreamDetails Details { get; init; } = StreamDetails.Empty;
    public VideoStreamInfo? Video { get; init; }
    public AudioStreamInfo? Audio { get; init; }

    public int SourceNumber => Key.SourceNumber;
    public int StreamIndex => Key.StreamIndex;

    public bool IsBitmapSubtitle => Kind == TrackKind.Subtitle && Codecs.IsBitmapSubtitle(CodecName);
    public bool IsTextSubtitle => Kind == TrackKind.Subtitle && !Codecs.IsBitmapSubtitle(CodecName);
}

/// <summary>What the user chose for one track.</summary>
public sealed record TrackSettings
{
    public bool Enabled { get; init; } = true;
    public string? Name { get; init; }
    public Language Language { get; init; } = Languages.Undetermined;
    public TrackFlags Flags { get; init; }

    /// <summary>Positive values play the track later, negative values earlier. Always 0 for video.</summary>
    public int TimeshiftMs { get; init; }
}

public sealed record RemuxTrack(SourceTrack Source, TrackSettings Settings)
{
    public TrackKey Key => Source.Key;
    public TrackKind Kind => Source.Kind;
    public bool Enabled => Settings.Enabled;
}

/// <summary>A probed source file. <see cref="Number"/> is the 1-based marker shown in the UI.</summary>
public sealed record RemuxSource
{
    public required int Number { get; init; }
    public required string Path { get; init; }
    public required SourceMediaInfo MediaInfo { get; init; }

    /// <summary>The FFmpeg character set of a standalone text subtitle file that is not UTF-8.</summary>
    public string? SubtitleCharset { get; init; }

    public IReadOnlyList<DataStreamInfo> Attachments =>
        [.. MediaInfo.DataStreams.Where(stream => stream.IsAttachment)];
}

/// <summary>Everything a remux needs: sources, tracks in output order, chapters, format, title and path.</summary>
public sealed record RemuxPlan
{
    public required IReadOnlyList<RemuxSource> Sources { get; init; }

    /// <summary>All tracks in output order (video, audio, subtitles), including disabled ones.</summary>
    public required IReadOnlyList<RemuxTrack> Tracks { get; init; }

    public int? ChapterSourceNumber { get; init; }
    public required OutputFormat Format { get; init; }
    public string? Title { get; init; }
    public string? OutputPath { get; init; }
    public bool OptimizeForStreaming { get; init; }

    /// <summary>Rewrites the Matroska track statistics with mkvpropedit after the remux, when it is installed.</summary>
    public bool UpdateMatroskaStatistics { get; init; } = true;

    public OutputFormatInfo FormatInfo => OutputFormats.Get(Format);

    public IReadOnlyList<RemuxTrack> EnabledTracks => [.. Tracks.Where(track => track.Enabled)];

    public RemuxSource GetSource(int number) =>
        Sources.FirstOrDefault(source => source.Number == number)
        ?? throw new InvalidOperationException($"Source {number} is not part of the plan.");
}
