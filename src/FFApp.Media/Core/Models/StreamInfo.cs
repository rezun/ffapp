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
    public StreamDetails Details { get; init; } = StreamDetails.Empty;
    public bool HasDolbyVision { get; init; }

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
    public string? ChannelLayout { get; init; }

    public ImmutableArray<string> MetadataKeys { get; init; } = [];
    public StreamDetails Details { get; init; } = StreamDetails.Empty;
}

public sealed record SubtitleStreamInfo(
    int Index,
    string? CodecName = null,
    string? Language = null)
{
    public StreamDetails Details { get; init; } = StreamDetails.Empty;
}

public sealed record DataStreamInfo(
    int Index,
    string? CodecName = null)
{
    public bool IsAttachment { get; init; }
    public string? FileName { get; init; }
    public string? MimeType { get; init; }
}

public sealed record AttachedPictureStreamInfo(
    int Index,
    string? CodecName = null,
    string? FileName = null,
    string? MimeType = null);

/// <summary>Track metadata FFprobe reports for every stream type: name, language, flags and raw tags.</summary>
public sealed record StreamDetails
{
    public static StreamDetails Empty { get; } = new();

    public string? Title { get; init; }
    public string? Language { get; init; }
    public StreamDisposition Disposition { get; init; } = StreamDisposition.None;
    public string? Profile { get; init; }
    public int? BitsPerRawSample { get; init; }
    public double? DurationSeconds { get; init; }
    public ImmutableDictionary<string, string> Tags { get; init; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The FFmpeg disposition flags that describe a track's role.</summary>
public sealed record StreamDisposition
{
    public static StreamDisposition None { get; } = new();

    public bool Default { get; init; }
    public bool Forced { get; init; }
    public bool HearingImpaired { get; init; }
    public bool VisualImpaired { get; init; }
    public bool Comment { get; init; }
    public bool Original { get; init; }
}

public sealed record ChapterInfo(double StartSeconds, double EndSeconds, string? Title);
