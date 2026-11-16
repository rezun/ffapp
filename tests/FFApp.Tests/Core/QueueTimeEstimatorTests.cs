using FFApp.Core;
using FFApp.Core.Models;
using FluentAssertions;

namespace FFApp.Tests.Core;

public class QueueTimeEstimatorTests
{
    private static QueueEstimateEntry Running(double media, double elapsed, double percentage, int passIndex = 1, int passCount = 1) =>
        new(JobState.Running, media, passCount,
            new JobProgress(JobState.Running, passIndex, passCount, elapsed, Percentage: percentage));

    private static QueueEstimateEntry Pending(double? media, int passCount = 1) =>
        new(JobState.Pending, media, passCount);

    [Fact]
    public void RunningJobRateAppliesToRemainingAndPendingWork()
    {
        // 100 s media, 25 % done after 50 s → 2 wall seconds per media second.
        var remaining = QueueTimeEstimator.EstimateRemainingSeconds(
            [Running(100, 50, 25), Pending(200), Pending(50)]);

        remaining.Should().BeApproximately(150 + 500, 0.001);
    }

    [Fact]
    public void TwoPassJobsCountPassesAndPerPassPercentage()
    {
        // Second pass at 50 %: 75 % of 2 × 100 s work done in 150 s → 1 wall second per work unit.
        var remaining = QueueTimeEstimator.EstimateRemainingSeconds(
            [Running(100, 150, 50, passIndex: 2, passCount: 2), Pending(100, passCount: 2)]);

        remaining.Should().BeApproximately(50 + 200, 0.001);
    }

    [Fact]
    public void CompletedJobsProvideRateWhileRunningJobJustStarted()
    {
        var completed = new QueueEstimateEntry(JobState.Completed, 100, 1, ElapsedSeconds: 50);

        var remaining = QueueTimeEstimator.EstimateRemainingSeconds(
            [completed, Running(100, 0, 0), Pending(100)]);

        remaining.Should().BeApproximately(50 + 50, 0.001);
    }

    public static TheoryData<QueueEstimateEntry[]> NoEstimateCases => new()
    {
        { [Running(100, 50, 25)] },
        { [Pending(100), Pending(100)] },
        { [Running(100, 50, 25), Pending(null)] },
        { [Running(100, 1, 0.5), Pending(100)] },
    };

    [Theory]
    [MemberData(nameof(NoEstimateCases))]
    public void ReturnsNullWithoutRunningAndPendingJobsOrEnoughData(QueueEstimateEntry[] entries)
    {
        QueueTimeEstimator.EstimateRemainingSeconds(entries).Should().BeNull();
    }
}
