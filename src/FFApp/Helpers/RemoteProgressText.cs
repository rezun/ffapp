using System.Globalization;

using FFApp.Core.Models;

namespace FFApp.Helpers;

/// <summary>Queue texts for the phases of a server job, such as "Uploading 42 % · 108 MB/s".</summary>
public static class RemoteProgressText
{
    public static double Percentage(RemoteProgress remote, JobProgress progress) => remote.Phase switch
    {
        RemotePhase.Uploading or RemotePhase.Downloading => remote.Percentage ?? 0,
        RemotePhase.Running => OverallPercent(progress),
        RemotePhase.Done => 100,
        RemotePhase.WaitingForServer => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(remote), remote.Phase, "Unknown remote phase."),
    };

    public static string Format(RemoteProgress remote, JobProgress progress, DateTimeOffset now)
    {
        var parts = new List<string>(4);
        switch (remote.Phase)
        {
            case RemotePhase.Uploading:
                parts.Add(remote.Percentage is { } up ? $"Uploading {Percent(up)}" : "Uploading");
                if (remote.BytesPerSecond is { } upRate && remote.Notice is null)
                    parts.Add(Rate(upRate));
                break;
            case RemotePhase.WaitingForServer:
                parts.Add("Waiting for server");
                if (remote.QueuePosition is { } position)
                    parts.Add(string.Create(CultureInfo.CurrentCulture, $"position {position}"));
                if (remote.EstimatedStartAt is { } start)
                    parts.Add(start <= now ? "starts soon" : $"starts in about {Duration((start - now).TotalSeconds)}");
                break;
            case RemotePhase.Running:
                var running = remote.IsPaused ? "Paused on server" : "Running";
                if (!remote.IsPaused && progress.Speed is { } speed)
                    running += string.Create(CultureInfo.CurrentCulture, $" {speed:0.0}×");
                parts.Add(running);
                parts.Add(Percent(OverallPercent(progress)));
                if (!remote.IsPaused && FormatHelpers.EstimateRemainingSeconds(progress.ElapsedSeconds, progress.Percentage) is { } left)
                {
                    // The estimate covers the current pass; later passes take about as long each.
                    var passesLeft = Math.Max(0, progress.PassCount - progress.PassIndex);
                    var perPass = progress.ElapsedSeconds / Math.Max(1, progress.PassIndex - 1 + (progress.Percentage ?? 0) / 100.0);
                    parts.Add($"{Duration(left + passesLeft * perPass)} left");
                }
                if (progress.PassCount > 1)
                    parts.Add(string.Create(CultureInfo.CurrentCulture, $"pass {progress.PassIndex}/{progress.PassCount}"));
                break;
            case RemotePhase.Downloading:
                parts.Add(remote.Percentage is { } down ? $"Downloading {Percent(down)}" : "Downloading");
                if (remote.BytesPerSecond is { } downRate && remote.Notice is null)
                    parts.Add(Rate(downRate));
                break;
            case RemotePhase.Done:
                parts.Add("Done");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(remote), remote.Phase, "Unknown remote phase.");
        }
        if (remote.Notice is { Length: > 0 } notice)
            parts.Add(notice);
        return string.Join(" · ", parts);
    }

    /// <summary>"1:40 h" from one hour on, otherwise "25 min" or "40 s".</summary>
    public static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalHours >= 1)
            return string.Create(CultureInfo.CurrentCulture, $"{(int)span.TotalHours}:{span.Minutes:D2} h");
        if (span.TotalMinutes >= 1)
            return string.Create(CultureInfo.CurrentCulture, $"{(int)Math.Round(span.TotalMinutes)} min");
        return string.Create(CultureInfo.CurrentCulture, $"{(int)span.TotalSeconds} s");
    }

    private static double OverallPercent(JobProgress progress)
    {
        var passes = Math.Max(1, progress.PassCount);
        return Math.Clamp((progress.PassIndex - 1 + (progress.Percentage ?? 0) / 100.0) / passes * 100, 0, 100);
    }

    private static string Percent(double value) => string.Create(CultureInfo.CurrentCulture, $"{value:0} %");

    private static string Rate(double bytesPerSecond) => bytesPerSecond >= 1_000_000
        ? string.Create(CultureInfo.CurrentCulture, $"{bytesPerSecond / 1_000_000:0} MB/s")
        : string.Create(CultureInfo.CurrentCulture, $"{bytesPerSecond / 1_000:0} kB/s");
}
