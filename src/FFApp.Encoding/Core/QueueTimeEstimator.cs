using FFApp.Core.Models;

namespace FFApp.Core;

/// <summary>
/// One queue entry as seen by <see cref="QueueTimeEstimator"/>.
/// </summary>
/// <param name="State">Current job state.</param>
/// <param name="MediaSeconds">Output duration in media seconds (after trimming), or null when unknown.</param>
/// <param name="PassCount">Number of encoding passes the job runs.</param>
/// <param name="Progress">Latest progress of a running job.</param>
/// <param name="ElapsedSeconds">Active encoding time of a completed job.</param>
public sealed record QueueEstimateEntry(
    JobState State,
    double? MediaSeconds,
    int PassCount,
    JobProgress? Progress = null,
    double? ElapsedSeconds = null);

/// <summary>
/// Estimates how long the queue needs until every pending job is encoded.
/// Work is measured in media seconds × passes, so long and two-pass jobs weigh more.
/// The running job uses its own throughput; pending jobs use the throughput of the
/// running job blended with jobs that already completed.
/// </summary>
public static class QueueTimeEstimator
{
    private static readonly double MinimumElapsedSeconds = 3;
    private static readonly double MinimumCompletedFraction = 0.01;

    /// <summary>
    /// Returns the estimated remaining seconds, or null when no job is running, nothing is
    /// pending after it, a duration is unknown, or there is not yet enough progress data.
    /// </summary>
    public static double? EstimateRemainingSeconds(IReadOnlyList<QueueEstimateEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var running = entries.FirstOrDefault(e => e.State == JobState.Running);
        if (running?.Progress is not { } progress || Work(running) is not { } runningWork)
            return null;

        double pendingWork = 0;
        var pendingCount = 0;
        foreach (var entry in entries)
        {
            if (entry.State != JobState.Pending)
                continue;
            if (Work(entry) is not { } work)
                return null;
            pendingWork += work;
            pendingCount++;
        }
        if (pendingCount == 0)
            return null;

        var passCount = Math.Max(1, running.PassCount);
        var passFraction = Math.Clamp((progress.Percentage ?? 0) / 100.0, 0, 1);
        var completedFraction = Math.Clamp((progress.PassIndex - 1 + passFraction) / passCount, 0, 1);
        var runningDone = runningWork * completedFraction;
        var runningElapsed = progress.ElapsedSeconds;

        double sampleWork = runningDone;
        double sampleElapsed = runningElapsed;
        foreach (var entry in entries)
        {
            if (entry.State == JobState.Completed
                && entry.ElapsedSeconds is > 0 and var elapsed
                && Work(entry) is { } work)
            {
                sampleWork += work;
                sampleElapsed += elapsed;
            }
        }

        var hasRunningRate = runningElapsed >= MinimumElapsedSeconds
            && completedFraction >= MinimumCompletedFraction;
        var hasHistory = sampleElapsed > runningElapsed;
        if (!hasRunningRate && !hasHistory)
            return null;

        var blendedRate = sampleWork / sampleElapsed;
        if (blendedRate <= 0)
            return null;
        var runningRate = hasRunningRate ? runningDone / runningElapsed : blendedRate;

        return runningWork * (1 - completedFraction) / runningRate + pendingWork / blendedRate;
    }

    private static double? Work(QueueEstimateEntry entry) =>
        entry.MediaSeconds is > 0 and var seconds ? seconds * Math.Max(1, entry.PassCount) : null;
}
