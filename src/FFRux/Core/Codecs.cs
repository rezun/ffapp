using System.Collections.Frozen;

namespace FFRux.Core;

/// <summary>Readable names for FFprobe codec names, and which subtitle codecs are bitmaps.</summary>
public static class Codecs
{
    private static readonly FrozenSet<string> BitmapSubtitles = FrozenSet.Create(
        StringComparer.Ordinal,
        "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "dvb_teletext", "xsub");

    private static readonly FrozenDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["h264"] = "H.264", ["hevc"] = "HEVC", ["av1"] = "AV1", ["vp9"] = "VP9", ["vp8"] = "VP8",
        ["mpeg2video"] = "MPEG-2", ["mpeg1video"] = "MPEG-1", ["mpeg4"] = "MPEG-4", ["vc1"] = "VC-1",
        ["prores"] = "ProRes", ["dnxhd"] = "DNxHD", ["mjpeg"] = "Motion JPEG", ["ffv1"] = "FFV1", ["theora"] = "Theora",
        ["aac"] = "AAC", ["ac3"] = "AC-3", ["eac3"] = "E-AC-3", ["dts"] = "DTS", ["truehd"] = "TrueHD",
        ["flac"] = "FLAC", ["alac"] = "ALAC", ["opus"] = "Opus", ["vorbis"] = "Vorbis", ["mp3"] = "MP3", ["mp2"] = "MP2",
        ["subrip"] = "SubRip", ["ass"] = "ASS", ["ssa"] = "SSA", ["webvtt"] = "WebVTT", ["mov_text"] = "MP4 text",
        ["hdmv_pgs_subtitle"] = "PGS", ["dvd_subtitle"] = "VobSub", ["dvb_subtitle"] = "DVB", ["dvb_teletext"] = "Teletext",
        ["microdvd"] = "MicroDVD", ["subviewer"] = "SubViewer", ["text"] = "Text",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static bool IsBitmapSubtitle(string codecName) => BitmapSubtitles.Contains(codecName);

    public static bool IsPcm(string codecName) =>
        codecName.StartsWith("pcm_", StringComparison.Ordinal)
        && codecName is not ("pcm_bluray" or "pcm_dvd");

    /// <summary>A short label such as "E-AC-3"; DTS profiles such as "DTS-HD MA" use the profile name.</summary>
    public static string Label(string codecName, string? profile = null)
    {
        if (codecName == "dts" && profile is { Length: > 0 } && profile.StartsWith("DTS", StringComparison.Ordinal))
            return profile;
        if (IsPcm(codecName))
            return "PCM";
        return Labels.GetValueOrDefault(codecName) ?? codecName.ToUpperInvariant();
    }

    public static bool StylesAreLost(string sourceCodec) => sourceCodec is "ass" or "ssa";
}
