namespace FFApp.Core.Models;

public sealed record VideoStreamInfo(
    int Index,
    string? CodecName = null,
    int? BitRate = null,
    int? Width = null,
    int? Height = null,
    double? FrameRate = null,
    int? FrameCount = null);

public sealed record AudioStreamInfo(
    int Index,
    string? CodecName = null,
    int? BitRate = null,
    int? Channels = null,
    string? Language = null);

public sealed record SubtitleStreamInfo(
    int Index,
    string? CodecName = null,
    string? Language = null);

public sealed record DataStreamInfo(
    int Index,
    string? CodecName = null);
