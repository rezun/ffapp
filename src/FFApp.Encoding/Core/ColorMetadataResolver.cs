using FFApp.Core.Models;

namespace FFApp.Core;

public static class ColorMetadataResolver
{
    public static string? KnownValue(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "unknown" or "unspecified" or "reserved" => null,
        var known => known,
    };

    public static ColorMetadata Source(VideoStreamInfo? video) => new(
        KnownValue(video?.ColorPrimaries), KnownValue(video?.ColorTransfer),
        KnownValue(video?.ColorSpace), KnownValue(video?.ColorRange));

    public static bool HasHdrSignal(VideoStreamInfo? video) =>
        video?.IsHdr == true || KnownValue(video?.ColorTransfer) is "smpte2084" or "arib-std-b67"
        || video?.HdrMetadata is not null;

    public static ColorMetadata Resolve(
        VideoStreamInfo? video, ColorMetadataSettings settings, HdrMode hdrMode)
    {
        if (video is null)
            return new ColorMetadata();

        var source = Source(video);
        // HDR processing determines the output colors. SDR overrides must not retag HDR.
        if (video.IsHdr)
        {
            return hdrMode switch
            {
                HdrMode.TonemapToSdr => new("bt709", "bt709", "bt709", "tv"),
                HdrMode.Preserve => source with
                {
                    Primaries = source.Primaries ?? "bt2020",
                    Transfer = source.Transfer ?? "smpte2084",
                    Matrix = source.Matrix ?? "bt2020nc",
                },
                _ => throw new ArgumentOutOfRangeException(nameof(hdrMode), hdrMode, "Unknown HDR mode."),
            };
        }

        // Incomplete HDR metadata must never receive an SDR fallback or override.
        if (HasHdrSignal(video))
            return source;

        return settings.Mode switch
        {
            ColorMetadataMode.Automatic => source,
            ColorMetadataMode.AssumeBt709 => new(
                source.Primaries ?? "bt709", source.Transfer ?? "bt709",
                source.Matrix ?? "bt709", source.Range ?? "tv"),
            ColorMetadataMode.Custom => new(settings.Primaries, settings.Transfer, settings.Matrix, settings.Range),
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Mode, "Unknown color metadata mode."),
        };
    }
}
