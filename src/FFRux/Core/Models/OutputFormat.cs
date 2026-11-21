using System.Text.Json.Serialization;
using FFApp.Core;
using FFRux.Core;

namespace FFRux.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<OutputFormat>))]
public enum OutputFormat
{
    [JsonStringEnumMemberName("mkv")] Mkv,
    [JsonStringEnumMemberName("mp4")] Mp4,
    [JsonStringEnumMemberName("mov")] Mov,
    [JsonStringEnumMemberName("webm")] Webm,
    [JsonStringEnumMemberName("ts")] Ts,
    [JsonStringEnumMemberName("mka")] Mka,
    [JsonStringEnumMemberName("m4a")] M4a,
    [JsonStringEnumMemberName("m4b")] M4b,
    [JsonStringEnumMemberName("ogg")] Ogg,
    [JsonStringEnumMemberName("opus")] Opus,
    [JsonStringEnumMemberName("mp3")] Mp3,
    [JsonStringEnumMemberName("flac")] Flac,
}

/// <summary>
/// How a format stores a track name. Ogg, Opus, MP3 and FLAC store none: their only title is the
/// container title, which players show as the song or book title.
/// </summary>
public enum TrackNameStorage
{
    None,
    Title,
    // MP4 and QuickTime have no track title; players show the handler name instead.
    HandlerName,
}

/// <summary>
/// Everything FFRux needs to know about an output format. The capabilities were checked against
/// FFmpeg 9 by muxing every field and reading it back with FFprobe.
/// </summary>
public sealed record OutputFormatInfo
{
    public required OutputFormat Format { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public required string Extension { get; init; }
    public required string Muxer { get; init; }
    public bool IsAudioOnly { get; init; }
    public bool IsMatroska { get; init; }
    public bool IsMp4Family { get; init; }
    public bool SupportsTitle { get; init; } = true;
    public bool SupportsChapters { get; init; } = true;
    public bool SupportsSubtitles { get; init; } = true;
    public bool SupportsLanguage { get; init; } = true;
    public TrackNameStorage TrackNames { get; init; }
    public TrackFlags SupportedFlags { get; init; }

    /// <summary>The subtitle codec text subtitles are converted to, or null if the format holds no text subtitles.</summary>
    public string? TextSubtitleCodec { get; init; }

    /// <summary>Exactly one audio track and nothing else (MP3, FLAC).</summary>
    public bool IsSingleAudioTrack { get; init; }

    public IReadOnlyList<string> MuxerArguments { get; init; } = [];
    public StreamingLayout StreamingLayout { get; init; }

    public string Label => string.Concat(Name, " (", Extension, ")");
}

public static class OutputFormats
{
    public static IReadOnlyList<OutputFormatInfo> All { get; } =
    [
        new()
        {
            Format = OutputFormat.Mkv, Name = "Matroska", ShortName = "MKV", Extension = ".mkv", Muxer = "matroska",
            IsMatroska = true, TrackNames = TrackNameStorage.Title, SupportedFlags = TrackFlagNames.All, TextSubtitleCodec = "subrip",
            StreamingLayout = StreamingLayout.CuesToFront,
        },
        new()
        {
            Format = OutputFormat.Mp4, Name = "MP4", ShortName = "MP4", Extension = ".mp4", Muxer = "mp4",
            IsMp4Family = true, TrackNames = TrackNameStorage.HandlerName, TextSubtitleCodec = "mov_text",
            SupportedFlags = TrackFlags.Default | TrackFlags.Forced | TrackFlags.HearingImpaired
                | TrackFlags.VisualImpaired | TrackFlags.Commentary,
            StreamingLayout = StreamingLayout.FastStart,
        },
        new()
        {
            Format = OutputFormat.Mov, Name = "QuickTime", ShortName = "MOV", Extension = ".mov", Muxer = "mov",
            IsMp4Family = true, TrackNames = TrackNameStorage.HandlerName, TextSubtitleCodec = "mov_text",
            SupportedFlags = TrackFlags.Default, StreamingLayout = StreamingLayout.FastStart,
        },
        new()
        {
            Format = OutputFormat.Webm, Name = "WebM", ShortName = "WebM", Extension = ".webm", Muxer = "webm",
            IsMatroska = true, TrackNames = TrackNameStorage.Title, TextSubtitleCodec = "webvtt",
            SupportedFlags = TrackFlags.Default | TrackFlags.Forced,
            StreamingLayout = StreamingLayout.CuesToFront,
        },
        new()
        {
            Format = OutputFormat.Ts, Name = "MPEG-TS", ShortName = "TS", Extension = ".ts", Muxer = "mpegts",
            SupportsTitle = false, SupportsChapters = false,
        },
        new()
        {
            Format = OutputFormat.Mka, Name = "Matroska audio", ShortName = "MKA", Extension = ".mka", Muxer = "matroska",
            IsAudioOnly = true, IsMatroska = true, TrackNames = TrackNameStorage.Title, SupportedFlags = TrackFlagNames.All,
            TextSubtitleCodec = "subrip",
        },
        new()
        {
            Format = OutputFormat.M4a, Name = "MPEG-4 audio", ShortName = "M4A", Extension = ".m4a", Muxer = "ipod",
            IsAudioOnly = true, IsMp4Family = true, SupportsSubtitles = false, TrackNames = TrackNameStorage.HandlerName,
            SupportedFlags = TrackFlags.Default, StreamingLayout = StreamingLayout.FastStart,
        },
        new()
        {
            Format = OutputFormat.M4b, Name = "MPEG-4 audiobook", ShortName = "M4B", Extension = ".m4b", Muxer = "ipod",
            IsAudioOnly = true, IsMp4Family = true, SupportsSubtitles = false, TrackNames = TrackNameStorage.HandlerName,
            SupportedFlags = TrackFlags.Default, MuxerArguments = ["-brand", "M4B "],
            StreamingLayout = StreamingLayout.FastStart,
        },
        new()
        {
            Format = OutputFormat.Ogg, Name = "Ogg", ShortName = "Ogg", Extension = ".ogg", Muxer = "ogg",
            IsAudioOnly = true, SupportsSubtitles = false,
        },
        new()
        {
            Format = OutputFormat.Opus, Name = "Opus", ShortName = "Opus", Extension = ".opus", Muxer = "opus",
            IsAudioOnly = true, SupportsSubtitles = false,
        },
        new()
        {
            Format = OutputFormat.Mp3, Name = "MP3", ShortName = "MP3", Extension = ".mp3", Muxer = "mp3",
            IsAudioOnly = true, SupportsSubtitles = false, SupportsLanguage = false, IsSingleAudioTrack = true,
        },
        new()
        {
            Format = OutputFormat.Flac, Name = "FLAC", ShortName = "FLAC", Extension = ".flac", Muxer = "flac",
            IsAudioOnly = true, SupportsSubtitles = false, SupportsLanguage = false, SupportsChapters = false,
            IsSingleAudioTrack = true,
        },
    ];

    public static OutputFormatInfo Get(OutputFormat format) =>
        All.FirstOrDefault(info => info.Format == format)
        ?? throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown output format.");
}
