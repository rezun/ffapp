using System.Globalization;
using FFApp.Core.Models;
using FFRux.Core.Models;

namespace FFRux.Core;

/// <summary>Turns a probed file into tracks, initial settings and readable descriptions.</summary>
public static class SourceTracks
{
    private const string UnknownCodec = "unknown";

    public static IReadOnlyList<SourceTrack> Create(int sourceNumber, SourceMediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var tracks = new List<SourceTrack>();
        foreach (var video in info.VideoStreams)
        {
            tracks.Add(new SourceTrack
            {
                Key = new TrackKey(sourceNumber, video.Index),
                Kind = TrackKind.Video,
                CodecName = video.CodecName ?? UnknownCodec,
                Details = video.Details,
                Video = video,
            });
        }
        foreach (var audio in info.AudioStreams)
        {
            tracks.Add(new SourceTrack
            {
                Key = new TrackKey(sourceNumber, audio.Index),
                Kind = TrackKind.Audio,
                CodecName = audio.CodecName ?? UnknownCodec,
                Details = audio.Details,
                Audio = audio,
            });
        }
        foreach (var subtitle in info.SubtitleStreams)
        {
            tracks.Add(new SourceTrack
            {
                Key = new TrackKey(sourceNumber, subtitle.Index),
                Kind = TrackKind.Subtitle,
                CodecName = subtitle.CodecName ?? UnknownCodec,
                Details = subtitle.Details,
            });
        }
        return tracks;
    }

    /// <summary>
    /// The settings a new track starts with: the values from the file, plus language and flags
    /// from the file name when the file holds only this track (an external audio or subtitle file).
    /// </summary>
    public static TrackSettings InitialSettings(SourceTrack track, FileNameHint hint)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(hint);
        var details = track.Details;
        var language = Languages.FromFileValue(details.Language);
        if (language == Languages.Undetermined && hint.Language is { } hinted)
            language = hinted;

        return new TrackSettings
        {
            Enabled = true,
            Name = TrackName(details),
            Language = language,
            Flags = FlagsFrom(details.Disposition) | hint.Flags,
        };
    }

    /// <summary>
    /// The track title, or for MP4 sources the handler name, unless it is a writer's placeholder
    /// such as "SoundHandler".
    /// </summary>
    internal static string? TrackName(StreamDetails details)
    {
        if (!string.IsNullOrWhiteSpace(details.Title))
            return details.Title;
        var handler = details.Tags.GetValueOrDefault("handler_name");
        if (string.IsNullOrWhiteSpace(handler)
            || handler.EndsWith("Handler", StringComparison.OrdinalIgnoreCase)
            || handler.StartsWith("Core Media", StringComparison.OrdinalIgnoreCase)
            || handler.StartsWith("ISO Media", StringComparison.OrdinalIgnoreCase)
            || handler.StartsWith("Mainconcept", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return handler.Trim();
    }

    public static TrackFlags FlagsFrom(StreamDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        var flags = TrackFlags.None;
        if (disposition.Default) flags |= TrackFlags.Default;
        if (disposition.Forced) flags |= TrackFlags.Forced;
        if (disposition.HearingImpaired) flags |= TrackFlags.HearingImpaired;
        if (disposition.VisualImpaired) flags |= TrackFlags.VisualImpaired;
        if (disposition.Comment) flags |= TrackFlags.Commentary;
        if (disposition.Original) flags |= TrackFlags.Original;
        return flags;
    }

    /// <summary>What a file name hint changed compared to the file itself, for the "Prefilled" line.</summary>
    public static string? DescribePrefill(SourceTrack track, FileNameHint hint)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(hint);
        var parts = new List<string>();
        if (hint.Language is not null && Languages.FromFileValue(track.Details.Language) == Languages.Undetermined)
            parts.Add("language");
        var added = hint.Flags & ~FlagsFrom(track.Details.Disposition);
        if (added != TrackFlags.None)
            parts.Add(TrackFlagNames.Describe(added));
        if (parts.Count == 0)
            return null;
        var text = string.Join(", ", parts);
        return string.Concat(char.ToUpperInvariant(text[0]).ToString(), text.AsSpan(1), " (from file name)");
    }

    /// <summary>The details column: resolution and HDR for video, channels and bitrate for audio, text or bitmap for subtitles.</summary>
    public static string Describe(SourceTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var parts = new List<string>();
        switch (track.Kind)
        {
            case TrackKind.Video when track.Video is { } video:
                if (video.Height is { } height)
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{height}p"));
                if (video.FrameRate is { } fps and > 0)
                    parts.Add(FormatFrameRate(fps));
                if (video.HdrFormat == HdrFormat.Hdr10)
                    parts.Add("HDR10");
                else if (video.HdrFormat == HdrFormat.Hlg)
                    parts.Add("HLG");
                if (video.HasDolbyVision)
                    parts.Add("Dolby Vision");
                break;
            case TrackKind.Audio when track.Audio is { } audio:
                if (ChannelLabel(audio) is { } channels)
                    parts.Add(channels);
                if (BitRate(track) is { } bitRate)
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bitRate / 1000.0)} kb/s"));
                else if (track.Details.BitsPerRawSample is { } bits and > 0)
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{bits} bit"));
                break;
            case TrackKind.Subtitle:
                parts.Add(track.IsBitmapSubtitle ? "Bitmap" : "Text");
                break;
        }
        return string.Join(" · ", parts);
    }

    public static string FormatTimeshift(int milliseconds) =>
        milliseconds > 0
            ? string.Create(CultureInfo.InvariantCulture, $"+{milliseconds} ms")
            : string.Create(CultureInfo.InvariantCulture, $"{milliseconds} ms");

    /// <summary>The bitrate from FFprobe, or from the statistics tags Matroska writers add.</summary>
    public static long? BitRate(SourceTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var probed = track.Kind switch
        {
            TrackKind.Video => track.Video?.BitRate,
            TrackKind.Audio => track.Audio?.BitRate,
            _ => null,
        };
        if (probed is { } value and > 0)
            return value;
        return TagNumber(track.Details, "BPS");
    }

    /// <summary>Reads a numeric tag, also in its language-suffixed form such as <c>BPS-eng</c>.</summary>
    internal static long? TagNumber(StreamDetails details, string name)
    {
        foreach (var (key, value) in details.Tags)
        {
            if ((key.Equals(name, StringComparison.OrdinalIgnoreCase) || key.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase))
                && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
        }
        return null;
    }

    private static string? ChannelLabel(AudioStreamInfo audio)
    {
        var layout = audio.ChannelLayout;
        if (layout is not null)
        {
            var parenthesis = layout.IndexOf('(', StringComparison.Ordinal);
            var core = parenthesis > 0 ? layout[..parenthesis] : layout;
            if (core is "mono") return "1.0";
            if (core is "stereo") return "2.0";
            if (core.Length > 0 && char.IsDigit(core[0])) return core;
        }
        return audio.Channels is { } channels
            ? string.Create(CultureInfo.InvariantCulture, $"{channels} ch")
            : null;
    }

    private static string FormatFrameRate(double fps)
    {
        var rounded = Math.Round(fps, 3);
        return Math.Abs(rounded - Math.Round(rounded)) < 0.001
            ? Math.Round(rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
