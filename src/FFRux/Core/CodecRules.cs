using System.Collections.Frozen;
using FFRux.Core.Models;

namespace FFRux.Core;

public enum CodecRuleKind
{
    Copy,
    Warning,
    Convert,
}

/// <summary>One cell of the copy matrix. <see cref="Note"/> explains a warning.</summary>
public sealed record CodecRule(CodecRuleKind Kind, string? Note = null);

/// <summary>
/// The copy matrix: which codec may be copied into which format. It is data on purpose, so the
/// integration tests can run every entry against FFmpeg. A missing entry means incompatible.
/// Text subtitles are not listed per format: they are copied when the format's own text codec
/// matches and converted otherwise (see <see cref="CompatibilityChecker"/>).
/// </summary>
public static class CodecRules
{
    private static readonly OutputFormat[] Matroska = [OutputFormat.Mkv, OutputFormat.Mka];
    private static readonly OutputFormat[] AppleAudio = [OutputFormat.M4a, OutputFormat.M4b];

    public static FrozenDictionary<string, FrozenDictionary<OutputFormat, CodecRule>> Video { get; } = Build(
    [
        ("h264", [Copy(OutputFormat.Mkv, OutputFormat.Mp4, OutputFormat.Mov, OutputFormat.Ts)]),
        ("hevc", [Copy(OutputFormat.Mkv, OutputFormat.Mp4, OutputFormat.Mov, OutputFormat.Ts)]),
        // FFmpeg 9 writes AV1 into MPEG-TS as an unknown data stream, so TS is left out.
        ("av1", [Copy(OutputFormat.Mkv, OutputFormat.Mp4, OutputFormat.Webm)]),
        ("vp9", [Copy(OutputFormat.Mkv, OutputFormat.Webm),
            Warn("Many players, including Apple devices, don't play VP9 in MP4.", OutputFormat.Mp4)]),
        ("vp8", [Copy(OutputFormat.Mkv, OutputFormat.Webm)]),
        ("mpeg2video", [Copy(OutputFormat.Mkv, OutputFormat.Mov, OutputFormat.Ts),
            Warn("Many players don't play MPEG-2 video in MP4.", OutputFormat.Mp4)]),
        ("mpeg4", [Copy(OutputFormat.Mkv, OutputFormat.Mp4, OutputFormat.Mov),
            Warn("Few players expect MPEG-4 Part 2 video in MPEG-TS.", OutputFormat.Ts)]),
        ("vc1", [Copy(OutputFormat.Mkv, OutputFormat.Ts)]),
        ("prores", [Copy(OutputFormat.Mkv, OutputFormat.Mov)]),
        ("dnxhd", [Copy(OutputFormat.Mkv, OutputFormat.Mov)]),
        ("mjpeg", [Copy(OutputFormat.Mkv, OutputFormat.Mov)]),
        ("ffv1", [Copy(OutputFormat.Mkv)]),
        ("theora", [Copy(OutputFormat.Mkv)]),
    ]);

    public static FrozenDictionary<string, FrozenDictionary<OutputFormat, CodecRule>> Audio { get; } = Build(
    [
        ("aac", [Copy([.. Matroska, OutputFormat.Mp4, OutputFormat.Mov, .. AppleAudio, OutputFormat.Ts])]),
        ("ac3", [Copy([.. Matroska, OutputFormat.Mp4, OutputFormat.Mov, .. AppleAudio, OutputFormat.Ts])]),
        ("eac3", [Copy([.. Matroska, OutputFormat.Mp4, OutputFormat.Mov, OutputFormat.Ts])]),
        ("dts", [Copy([.. Matroska, OutputFormat.Ts]),
            Warn("Apple devices and many TVs don't play DTS in MP4 or QuickTime files.", OutputFormat.Mp4, OutputFormat.Mov)]),
        ("truehd", [Copy([.. Matroska, OutputFormat.Ts])]),
        ("flac", [Copy([.. Matroska, OutputFormat.Ogg, OutputFormat.Flac]),
            Warn("Many players, including older Apple devices, don't play FLAC in MP4.", OutputFormat.Mp4)]),
        ("alac", [Copy([.. Matroska, OutputFormat.Mp4, OutputFormat.Mov, .. AppleAudio])]),
        ("opus", [Copy([.. Matroska, OutputFormat.Webm, OutputFormat.Ogg, OutputFormat.Opus]),
            Warn("Many players, including older Apple devices, don't play Opus in MP4.", OutputFormat.Mp4),
            Warn("Few players support Opus in MPEG-TS.", OutputFormat.Ts)]),
        ("vorbis", [Copy([.. Matroska, OutputFormat.Webm, OutputFormat.Ogg])]),
        ("mp3", [Copy([.. Matroska, OutputFormat.Mp4, OutputFormat.Mov, OutputFormat.Ts, OutputFormat.Mp3])]),
        // MP4 has no own code for MP2; FFmpeg reads it back as MP3, so MP4 is left out.
        ("mp2", [Copy([.. Matroska, OutputFormat.Mov, OutputFormat.Ts])]),
        (PcmKey, [Copy([.. Matroska, OutputFormat.Mov]),
            Warn("Few players support PCM audio in MP4.", OutputFormat.Mp4)]),
    ]);

    /// <summary>Bitmap subtitles: copy only, never converted.</summary>
    public static FrozenDictionary<string, FrozenDictionary<OutputFormat, CodecRule>> BitmapSubtitles { get; } = Build(
    [
        ("hdmv_pgs_subtitle", [Copy(Matroska)]),
        ("dvd_subtitle", [Copy(Matroska)]),
        ("dvb_subtitle", [Copy([.. Matroska, OutputFormat.Ts])]),
        ("dvb_teletext", [Copy(OutputFormat.Ts)]),
    ]);

    /// <summary>Uncompressed PCM variants share one row.</summary>
    public const string PcmKey = "pcm_*";

    public static CodecRule? Find(TrackKind kind, string codecName, OutputFormat format)
    {
        var table = kind switch
        {
            TrackKind.Video => Video,
            TrackKind.Audio => Audio,
            TrackKind.Subtitle => BitmapSubtitles,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        var key = kind == TrackKind.Audio && Codecs.IsPcm(codecName) ? PcmKey : codecName;
        return table.TryGetValue(key, out var row) ? row.GetValueOrDefault(format) : null;
    }

    public static bool IsKnown(TrackKind kind, string codecName) => kind switch
    {
        TrackKind.Video => Video.ContainsKey(codecName),
        TrackKind.Audio => Audio.ContainsKey(Codecs.IsPcm(codecName) ? PcmKey : codecName),
        TrackKind.Subtitle => !Codecs.IsBitmapSubtitle(codecName) || BitmapSubtitles.ContainsKey(codecName),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static (OutputFormat[] Formats, CodecRule Rule) Copy(params OutputFormat[] formats) =>
        (formats, new CodecRule(CodecRuleKind.Copy));

    private static (OutputFormat[] Formats, CodecRule Rule) Warn(string note, params OutputFormat[] formats) =>
        (formats, new CodecRule(CodecRuleKind.Warning, note));

    private static FrozenDictionary<string, FrozenDictionary<OutputFormat, CodecRule>> Build(
        (string Codec, (OutputFormat[] Formats, CodecRule Rule)[] Entries)[] rows)
    {
        var table = new Dictionary<string, FrozenDictionary<OutputFormat, CodecRule>>(StringComparer.Ordinal);
        foreach (var (codec, entries) in rows)
        {
            var row = new Dictionary<OutputFormat, CodecRule>();
            foreach (var (formats, rule) in entries)
            {
                foreach (var format in formats)
                    row.Add(format, rule);
            }
            table.Add(codec, row.ToFrozenDictionary());
        }
        return table.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
