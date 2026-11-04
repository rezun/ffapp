using System.Collections.Immutable;

namespace FFApp.Core.Models;

/// <summary>Null selects all audio streams; an empty array selects none. Indices are source stream indices.</summary>
public static class AudioTrackSelection
{
    public static SourceMediaInfo Apply(SourceMediaInfo mediaInfo, ImmutableArray<int>? indices) =>
        indices is { } selected
            ? mediaInfo with { AudioStreams = [.. mediaInfo.AudioStreams.Where(s => selected.Contains(s.Index))] }
            : mediaInfo;
}

/// <summary>Null selects all subtitle streams; an empty array selects none. Indices are source stream indices.</summary>
public static class SubtitleTrackSelection
{
    public static SourceMediaInfo Apply(SourceMediaInfo mediaInfo, ImmutableArray<int>? indices) =>
        indices is { } selected
            ? mediaInfo with { SubtitleStreams = [.. mediaInfo.SubtitleStreams.Where(s => selected.Contains(s.Index))] }
            : mediaInfo;
}

/// <summary>Restricts the source to the selected audio and subtitle tracks.</summary>
public static class TrackSelection
{
    public static SourceMediaInfo Apply(
        SourceMediaInfo mediaInfo,
        ImmutableArray<int>? audioIndices,
        ImmutableArray<int>? subtitleIndices) =>
        SubtitleTrackSelection.Apply(AudioTrackSelection.Apply(mediaInfo, audioIndices), subtitleIndices);
}
