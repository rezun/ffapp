using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace FFApp.Core.Models;

public sealed record SourceMediaInfo
{
    public required string SourcePath { get; init; }
    public string? ContainerFormat { get; init; }
    public double? DurationSeconds { get; init; }
    public int? BitRate { get; init; }
    public long? SizeBytes { get; init; }
    public string? Title { get; init; }
    public ImmutableArray<ChapterInfo> Chapters { get; init; } = [];
    public ImmutableArray<string> MetadataKeys { get; init; } = [];
    public ImmutableArray<VideoStreamInfo> VideoStreams { get; init; } = [];
    public ImmutableArray<AttachedPictureStreamInfo> AttachedPictureStreams { get; init; } = [];
    public ImmutableArray<AudioStreamInfo> AudioStreams { get; init; } = [];
    public ImmutableArray<SubtitleStreamInfo> SubtitleStreams { get; init; } = [];
    public ImmutableArray<DataStreamInfo> DataStreams { get; init; } = [];

    [JsonIgnore]
    public VideoStreamInfo? PrimaryVideoStream =>
        VideoStreams.Length > 0 ? VideoStreams[0] : null;

    [JsonIgnore]
    public int StreamCount =>
        VideoStreams.Length + AttachedPictureStreams.Length + AudioStreams.Length + SubtitleStreams.Length + DataStreams.Length;
}
