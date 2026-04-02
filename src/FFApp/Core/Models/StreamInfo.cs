namespace FFApp.Core.Models;

public sealed record VideoStreamInfo(
    int Index,
    string? CodecName = null,
    int? BitRate = null,
    int? Width = null,
    int? Height = null,
    double? FrameRate = null,
    int? FrameCount = null,
    int? Rotation = null)
{
    public int? DisplayWidth => IsRotated90Or270 ? Height : Width;
    public int? DisplayHeight => IsRotated90Or270 ? Width : Height;

    private bool IsRotated90Or270 =>
        Rotation is not null && ((Rotation.Value % 360 + 360) % 360) is 90 or 270;
}

public sealed record AudioStreamInfo(
    int Index,
    string? CodecName = null,
    int? BitRate = null,
    int? Channels = null,
    int? SampleRate = null,
    string? Language = null);

public sealed record SubtitleStreamInfo(
    int Index,
    string? CodecName = null,
    string? Language = null);

public sealed record DataStreamInfo(
    int Index,
    string? CodecName = null);
