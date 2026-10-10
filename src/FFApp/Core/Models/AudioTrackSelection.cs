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
