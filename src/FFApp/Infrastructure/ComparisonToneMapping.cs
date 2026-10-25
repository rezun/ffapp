using System.Globalization;
using FFmpeg.AutoGen;

namespace FFApp.Infrastructure;

/// <summary>How a decoder turns PQ/HLG frames into displayable SDR.</summary>
internal enum HdrConversion
{
    /// <summary>Show HDR frames as plain BGRA (flat); used when no conversion filters are available.</summary>
    None,
    /// <summary>zscale and tonemap baked into a Hald CLUT on the CPU.</summary>
    Clut,
    /// <summary>VideoToolbox decoding and tone mapping on the GPU (macOS).</summary>
    VideoToolbox,
}

/// <summary>
/// HDR→SDR conversion for comparison playback. The preview surface is 8-bit sRGB, so PQ/HLG frames
/// are linearized, mapped from BT.2020 to BT.709 and tone-mapped before display. Both sides of a
/// comparison use the same chain, so differences between them are preserved.
/// </summary>
/// <remarks>
/// On macOS, VideoToolbox converts on the GPU. Elsewhere zscale and tonemap would be too slow on every
/// 4K frame, so the chain runs once over a Hald identity image and every frame is mapped through that CLUT.
/// </remarks>
internal static class ComparisonToneMapping
{
    /// <summary>144³ entries; level 8 deviates clearly from direct tone mapping on saturated colors, level 16 needs ~200 MB of float LUT per decoder.</summary>
    public static readonly int ClutLevel = 12;
    private static readonly double ReferenceWhiteNits = 100;

    private static readonly string[] RequiredFilters =
        ["buffer", "buffersink", "format", "setparams", "trim", "zscale", "tonemap", "haldclutsrc", "haldclut"];

    private static readonly string[] VideoToolboxFilters = ["buffer", "buffersink", "format", "hwupload", "hwdownload", "scale_vt"];
    private static readonly string VideoToolboxConversion =
        "scale_vt=color_matrix=bt709:color_primaries=bt709:color_transfer=bt709,hwdownload,format=p010le";

    public static bool IsHdr(AVColorTransferCharacteristic transfer) =>
        transfer is AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084
            or AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;

    /// <summary>
    /// Graph between the buffer source labelled <c>in</c> and the sink labelled <c>out</c>; the sink receives
    /// full-range RGB48. The final narrowing to BGRA happens outside the graph, because libavfilter's
    /// automatic format conversion is single-threaded and would cost more than the tone mapping itself.
    /// </summary>
    /// <param name="peakNits">Content peak from HDR metadata; null lets tonemap pick its default for the transfer.</param>
    public static string FilterGraph(string transfer, string primaries, double? peakNits)
    {
        var tonemap = peakNits is > 0 and var peak
            ? string.Create(CultureInfo.InvariantCulture, $"tonemap=hable:peak={peak / ReferenceWhiteNits:0.###}")
            : "tonemap=hable";
        var clut = string.Join(",",
            string.Create(CultureInfo.InvariantCulture, $"haldclutsrc=level={ClutLevel}"),
            "trim=end_frame=1",
            "format=gbrp16le",
            $"setparams=colorspace=gbr:range=pc:color_trc={transfer}:color_primaries={primaries}",
            "zscale=t=linear:npl=100:p=bt709",
            "format=gbrpf32le",
            tonemap,
            "zscale=t=bt709:r=full",
            // haldclut mis-orders planar GBR input, so both inputs use packed RGB.
            "format=rgb48le");
        return $"{clut}[clut];[in]zscale=r=full,format=rgb48le[video];"
            + "[video][clut]haldclut=interp=tetrahedral:repeatlast=1[out]";
    }

    /// <summary>
    /// Graph from <c>in</c> to <c>out</c> that converts on the GPU and returns limited-range BT.709 P010.
    /// Software-decoded frames (codecs the GPU cannot decode) are uploaded first, so both sides of a
    /// comparison always use the same tone mapping.
    /// </summary>
    public static string VideoToolboxFilterGraph(bool hardwareFrames) => hardwareFrames
        ? $"[in]{VideoToolboxConversion}[out]"
        : $"[in]format=p010le,hwupload,{VideoToolboxConversion}[out]";

    public static bool IsVideoToolboxAvailable() => IsAvailable(VideoToolboxFilters);

    /// <summary>
    /// libavfilter and zimg are optional at runtime. Without them HDR frames fall back to the plain
    /// BGRA conversion, which shows them flat but keeps comparison playback working.
    /// </summary>
    public static bool IsAvailable() => IsAvailable(RequiredFilters);

    private static bool IsAvailable(string[] filters)
    {
        try
        {
            return (ffmpeg.avfilter_version() >> 16) == ffmpeg.LIBAVFILTER_VERSION_MAJOR
                && filters.All(IsFilterAvailable);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    private static unsafe bool IsFilterAvailable(string name) => ffmpeg.avfilter_get_by_name(name) != null;
}
