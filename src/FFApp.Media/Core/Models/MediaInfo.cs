using System.Collections.Immutable;

namespace FFApp.Core.Models;

public sealed record SourceMediaInfo
{
    public required string SourcePath { get; init; }
    public string? ContainerFormat { get; init; }
    public double? DurationSeconds { get; init; }
    public int? BitRate { get; init; }
    public ImmutableArray<string> MetadataKeys { get; init; } = [];
    public ImmutableArray<VideoStreamInfo> VideoStreams { get; init; } = [];
    public ImmutableArray<AttachedPictureStreamInfo> AttachedPictureStreams { get; init; } = [];
    public ImmutableArray<AudioStreamInfo> AudioStreams { get; init; } = [];
    public ImmutableArray<SubtitleStreamInfo> SubtitleStreams { get; init; } = [];
    public ImmutableArray<DataStreamInfo> DataStreams { get; init; } = [];

    public VideoStreamInfo? PrimaryVideoStream =>
        VideoStreams.Length > 0 ? VideoStreams[0] : null;

    public int StreamCount =>
        VideoStreams.Length + AttachedPictureStreams.Length + AudioStreams.Length + SubtitleStreams.Length + DataStreams.Length;
}
