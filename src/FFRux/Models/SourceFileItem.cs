using System.Globalization;
using FFApp.Core.Models;

namespace FFRux.Models;

/// <summary>One opened source file and a one-line description of what FFprobe found in it.</summary>
public sealed record SourceFileItem(string Path, string Summary, bool IsError)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public static SourceFileItem FromProbe(SourceMediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new SourceFileItem(info.SourcePath, Describe(info), IsError: false);
    }

    public static SourceFileItem Failed(string path, string message) =>
        new(path, message, IsError: true);

    internal static string Describe(SourceMediaInfo info)
    {
        var otherCount = info.AttachedPictureStreams.Length + info.DataStreams.Length;
        string?[] counts =
        [
            Count(info.VideoStreams.Length, "video"),
            Count(info.AudioStreams.Length, "audio"),
            Count(info.SubtitleStreams.Length, "subtitle"),
            Count(otherCount, "other"),
        ];
        var streams = string.Join(", ", counts.Where(c => c is not null));
        var description = streams.Length == 0 ? "No streams" : $"Streams: {streams}";

        return info.DurationSeconds is { } seconds and > 0
            ? $"{description} · {FormatDuration(seconds)}"
            : description;
    }

    private static string? Count(int count, string kind) =>
        count > 0 ? string.Create(CultureInfo.InvariantCulture, $"{count} {kind}") : null;

    internal static string FormatDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Round(seconds));
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
