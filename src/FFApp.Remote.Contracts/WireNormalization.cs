using System.Collections.Immutable;

using FFApp.Core.Models;

namespace FFApp.Remote.Contracts;

/// <summary>
/// The source-generated serializer sets <c>init</c> properties that are missing from the JSON to
/// <c>default</c> instead of their initializer, leaving default <see cref="ImmutableArray{T}"/>
/// values and null details. Clients always send complete objects; this restores the defaults for
/// partial input, so a sparse request is handled instead of crashing the agent.
/// </summary>
public static class WireNormalization
{
    public static SourceMediaInfo Normalize(SourceMediaInfo media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return media with
        {
            Chapters = OrEmpty(media.Chapters),
            MetadataKeys = OrEmpty(media.MetadataKeys),
            VideoStreams = [.. OrEmpty(media.VideoStreams).Select(stream => stream with
            {
                MetadataKeys = OrEmpty(stream.MetadataKeys),
                Details = Normalize(stream.Details),
            })],
            AttachedPictureStreams = OrEmpty(media.AttachedPictureStreams),
            AudioStreams = [.. OrEmpty(media.AudioStreams).Select(stream => stream with
            {
                MetadataKeys = OrEmpty(stream.MetadataKeys),
                Details = Normalize(stream.Details),
            })],
            SubtitleStreams = [.. OrEmpty(media.SubtitleStreams).Select(stream => stream with { Details = Normalize(stream.Details) })],
            DataStreams = OrEmpty(media.DataStreams),
        };
    }

    // Null is possible here despite the annotation: see the class remarks.
    private static StreamDetails Normalize(StreamDetails? details) => details is null
        ? StreamDetails.Empty
        : details with
        {
            Disposition = details.Disposition ?? StreamDisposition.None,
            Tags = details.Tags ?? ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
        };

    private static ImmutableArray<T> OrEmpty<T>(ImmutableArray<T> values) => values.IsDefault ? [] : values;
}
