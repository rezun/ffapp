using System.Collections.Immutable;

namespace FFApp.Core.Models;

public sealed record VideoStreamInfo(
    int Index,
    string? CodecName = null,
    int? BitRate = null,
    int? Width = null,
    int? Height = null,
    double? FrameRate = null,
    int? FrameCount = null,
    int? Rotation = null,
    string? PixelFormat = null,
    string? ColorPrimaries = null,
    string? ColorTransfer = null,
    string? ColorSpace = null,
    string? ColorRange = null,
    int? BitsPerRawSample = null,
    HdrMetadata? HdrMetadata = null)
{
    public ImmutableArray<string> MetadataKeys { get; init; } = [];

    public int? DisplayWidth => IsRotated90Or270 ? Height : Width;
    public int? DisplayHeight => IsRotated90Or270 ? Width : Height;

    public HdrFormat HdrFormat => (ColorTransfer, ColorPrimaries) switch
    {
        ("smpte2084", "bt2020") => HdrFormat.Hdr10,
        ("arib-std-b67", "bt2020") => HdrFormat.Hlg,
        _ => HdrFormat.Sdr,
    };

    public bool IsHdr => HdrFormat != HdrFormat.Sdr;

    private bool IsRotated90Or270 =>
        Rotation is not null && ((Rotation.Value % 360 + 360) % 360) is 90 or 270;
}

public sealed record HdrMetadata(
    int? MaxContentLightLevel,
    int? MaxAverageLightLevel,
    MasteringDisplayMetadata? MasteringDisplay);

public sealed record MasteringDisplayMetadata(
    string RedX, string RedY,
    string GreenX, string GreenY,
    string BlueX, string BlueY,
    string WhitePointX, string WhitePointY,
    string MinLuminance, string MaxLuminance);

public sealed record AudioStreamInfo(
    int Index,
    string? CodecName = null,
    int? BitRate = null,
    int? Channels = null,
    int? SampleRate = null,
    string? Language = null)
{
    public ImmutableArray<string> MetadataKeys { get; init; } = [];
}

public sealed record SubtitleStreamInfo(
    int Index,
    string? CodecName = null,
    string? Language = null);

public sealed record DataStreamInfo(
    int Index,
    string? CodecName = null);
