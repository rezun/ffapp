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
/// <param name="Kind">Groups jobs with similar throughput, such as the same codec and preset.</param>
public sealed record QueueEstimateEntry(
    JobState State,
    double? MediaSeconds,
    int PassCount,
    JobProgress? Progress = null,
    double? ElapsedSeconds = null,
    string? Kind = null);

/// <summary>
/// Estimated timing of a queue in seconds from now: the remaining time of the running job,
/// when each waiting job starts, and when the queue becomes idle. Null where unknown.
/// </summary>
public sealed record QueueSchedule(
    double? RunningRemainingSeconds,
    IReadOnlyList<double?> PendingStartSeconds,
    double? IdleAfterSeconds);

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

    /// <summary>
    /// Estimates when each waiting job starts. Each waiting job uses the throughput of completed
    /// jobs of the same <see cref="QueueEstimateEntry.Kind"/> (plus the running job when it is of
    /// that kind) and falls back to all completed jobs blended with the running job, because
    /// encoders such as AV1 and x264 differ greatly in speed.
    /// </summary>
    /// <param name="running">The running job, or null when nothing runs.</param>
    /// <param name="pending">Waiting jobs in start order.</param>
    /// <param name="history">Recently completed jobs with their active encoding time.</param>
    public static QueueSchedule EstimateSchedule(
        QueueEstimateEntry? running,
        IReadOnlyList<QueueEstimateEntry> pending,
        IReadOnlyList<QueueEstimateEntry> history)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(history);

        (double Work, double Elapsed, string? Kind)? runningSample = null;
        double? runningRemaining = 0;
        if (running is not null)
        {
            runningRemaining = null;
            if (Work(running) is { } runningWork)
            {
                var progress = running.Progress;
                var passCount = Math.Max(1, running.PassCount);
                var passFraction = Math.Clamp((progress?.Percentage ?? 0) / 100.0, 0, 1);
                var completedFraction = progress is null ? 0
                    : Math.Clamp((progress.PassIndex - 1 + passFraction) / passCount, 0, 1);
                var elapsed = progress?.ElapsedSeconds ?? 0;
                var hasOwnRate = elapsed >= MinimumElapsedSeconds && completedFraction >= MinimumCompletedFraction;
                if (hasOwnRate)
                    runningSample = (runningWork * completedFraction, elapsed, running.Kind);
                var rate = hasOwnRate ? runningWork * completedFraction / elapsed : RateFor(running.Kind);
                if (rate is > 0)
                    runningRemaining = runningWork * (1 - completedFraction) / rate;
            }
        }

        var starts = new double?[pending.Count];
        double? clock = runningRemaining;
        for (var i = 0; i < pending.Count; i++)
        {
            starts[i] = clock;
            if (clock is null)
                continue;
            var rate = RateFor(pending[i].Kind);
            clock = Work(pending[i]) is { } work && rate is > 0 ? clock + work / rate : null;
        }
        return new QueueSchedule(runningRemaining, starts, clock);

        double? RateFor(string? kind)
        {
            if (kind is not null && Rate(sample => string.Equals(sample.Kind, kind, StringComparison.Ordinal)) is { } similar)
                return similar;
            return Rate(_ => true);
        }

        double? Rate(Func<(double Work, double Elapsed, string? Kind), bool> include)
        {
            double work = 0, elapsed = 0;
            foreach (var entry in history)
            {
                if (entry.ElapsedSeconds is > 0 and var seconds && Work(entry) is { } done
                    && include((done, seconds, entry.Kind)))
                {
                    work += done;
                    elapsed += seconds;
                }
            }
            if (runningSample is { } sample && include(sample))
            {
                work += sample.Work;
                elapsed += sample.Elapsed;
            }
            return elapsed > 0 && work > 0 ? work / elapsed : null;
        }
    }

    private static double? Work(QueueEstimateEntry entry) =>
        entry.MediaSeconds is > 0 and var seconds ? seconds * Math.Max(1, entry.PassCount) : null;
}
