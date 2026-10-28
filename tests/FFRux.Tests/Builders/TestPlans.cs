using System.Collections.Immutable;
using FFApp.Core.Models;
using FFRux.Core;
using FFRux.Core.Models;

namespace FFRux.Tests.Builders;

/// <summary>Small probed files and plans for unit tests, without FFmpeg.</summary>
internal static class TestPlans
{
    public static VideoStreamInfo Video(int index, string codec = "h264") =>
        new(index, codec, Width: 1920, Height: 1080, FrameRate: 23.976);

    public static AudioStreamInfo Audio(int index, string codec = "aac", string? language = "eng", StreamDisposition? disposition = null) =>
        new(index, codec, Channels: 2, SampleRate: 48000, Language: language)
        {
            ChannelLayout = "stereo",
            Details = new StreamDetails { Language = language, Disposition = disposition ?? StreamDisposition.None },
        };

    public static SubtitleStreamInfo Subtitle(int index, string codec = "subrip", string? language = "eng") =>
        new(index, codec, language) { Details = new StreamDetails { Language = language } };

    public static SourceMediaInfo Media(
        string path,
        IEnumerable<VideoStreamInfo>? video = null,
        IEnumerable<AudioStreamInfo>? audio = null,
        IEnumerable<SubtitleStreamInfo>? subtitles = null,
        string format = "matroska,webm") => new()
        {
            SourcePath = path,
            ContainerFormat = format,
            DurationSeconds = 60,
            VideoStreams = [.. video ?? []],
            AudioStreams = [.. audio ?? []],
            SubtitleStreams = [.. subtitles ?? []],
        };

    /// <summary>A movie with H.264 video, English AAC audio and English SubRip subtitles.</summary>
    public static SourceMediaInfo Movie(string path = "/media/movie.mkv") =>
        Media(path, [Video(0)], [Audio(1)], [Subtitle(2)]);

    public static RemuxSource Source(int number, SourceMediaInfo info, string? charset = null) => new()
    {
        Number = number,
        Path = info.SourcePath,
        MediaInfo = info,
        SubtitleCharset = charset,
    };

    /// <summary>A plan with every track of the sources enabled, in output order.</summary>
    public static RemuxPlan Plan(OutputFormat format, params RemuxSource[] sources)
    {
        var tracks = sources
            .SelectMany(source => SourceTracks.Create(source.Number, source.MediaInfo))
            .OrderBy(track => track.Kind)
            .Select(track => new RemuxTrack(track, SourceTracks.InitialSettings(track, FileNameHint.None)))
            .ToList();
        return new RemuxPlan
        {
            Sources = sources,
            Tracks = tracks,
            Format = format,
            OutputPath = "/out/result" + OutputFormats.Get(format).Extension,
        };
    }

    public static RemuxPlan Plan(OutputFormat format, SourceMediaInfo info) => Plan(format, Source(1, info));

    public static RemuxPlan WithTrack(this RemuxPlan plan, int sourceNumber, int streamIndex, Func<TrackSettings, TrackSettings> change) =>
        plan with
        {
            Tracks = [.. plan.Tracks.Select(track => track.Key == new TrackKey(sourceNumber, streamIndex)
                ? track with { Settings = change(track.Settings) }
                : track)],
        };

    public static TrackCompatibility Status(this CompatibilityReport report, int sourceNumber, int streamIndex) =>
        report.For(new TrackKey(sourceNumber, streamIndex))
        ?? throw new InvalidOperationException($"No status for {sourceNumber}:{streamIndex}.");

    public static ImmutableArray<string> Args(this RemuxCommands commands) => commands.Ffmpeg.Arguments;
}
