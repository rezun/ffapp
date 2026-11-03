using System.Collections.Immutable;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure;
using FFApp.Infrastructure.Logging;
using FFApp.Tests.Builders;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.Infrastructure;

public class QueueServiceTests
{
    private readonly IJobExecutionService _mockExecution = Substitute.For<IJobExecutionService>();
    private readonly IJobLogStore _jobLogs = Substitute.For<IJobLogStore>();
    private readonly QueueService _sut;

    public QueueServiceTests()
    {
        _jobLogs.Start(Arg.Any<string>()).Returns(_ => JobLog.Discarding);
        _sut = new QueueService(_ => _mockExecution, _jobLogs, TimeProvider.System);
        SetupDefaultExecution();
    }

    private void SetupDefaultExecution()
    {
        _mockExecution.ExecuteAsync(
            Arg.Any<JobDefinition>(),
            Arg.Any<SourceMediaInfo>(),
            Arg.Any<Action<JobProgress>?>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<ExecutionPauseState?>(),
            Arg.Any<CancellationToken>())
        .Returns(call =>
        {
            var job = call.Arg<JobDefinition>();
            return Task.FromResult(new JobResult(
                job, JobState.Completed, [], [], 1.0, ""));
        });
    }

    private static (string Id, QueuedJob Job) MakeQueueEntry(string? id = null)
    {
        var job = new TestJobDefinitionBuilder().Build();
        var media = new TestMediaInfoBuilder().Build();
        var queued = new QueuedJob(job, media, [], new ValidationResult());
        return (id ?? Guid.NewGuid().ToString(), queued);
    }

    // --- Job logs ---

    [Fact]
    public async Task ExecuteAll_WritesEncoderOutputAndResultToTheJobLog()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ffapp-queue-logs-{Guid.NewGuid():N}");
        try
        {
            var store = new JobLogStore(
                new JobLogOptions(directory, TimeSpan.FromDays(30), "FFApp tests"),
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<JobLogStore>.Instance);
            var sut = new QueueService(_ => _mockExecution, store, TimeProvider.System);
            _mockExecution.ExecuteAsync(
                    Arg.Any<JobDefinition>(), Arg.Any<SourceMediaInfo>(), Arg.Any<Action<JobProgress>?>(),
                    Arg.Any<Action<string>?>(), Arg.Any<ExecutionPauseState?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    call.ArgAt<Action<string>?>(3)?.Invoke("frame=  42 fps=24");
                    return Task.FromResult(new JobResult(call.Arg<JobDefinition>(), JobState.Completed, [], [], 83, ""));
                });
            var (id, job) = MakeQueueEntry("a");
            sut.Enqueue([(id, job)]);
            var started = new List<QueueItem>();
            var uiLines = new List<string>();

            await sut.ExecuteAllAsync(itemStarted: started.Add, logCallback: (_, line) => uiLines.Add(line));

            var logPath = sut.Items.Single().LogPath;
            logPath.Should().NotBeNull().And.StartWith(directory);
            started.Single().LogPath.Should().Be(logPath, "the UI learns the path when the job starts");
            uiLines.Should().Equal("frame=  42 fps=24");
            var saved = await File.ReadAllTextAsync(logPath!);
            saved.Should().Contain($"Source:    {job.Job.SourcePath}")
                .And.Contain("frame=  42 fps=24")
                .And.Contain("Result:    Completed")
                .And.Contain("Elapsed:   00:01:23");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResetToPending_ClearsTheLogPath()
    {
        _jobLogs.Start(Arg.Any<string>()).Returns(_ => new JobLogStore(
                new JobLogOptions(Path.Combine(Path.GetTempPath(), $"ffapp-queue-logs-{Guid.NewGuid():N}"), TimeSpan.FromDays(1), "t"),
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<JobLogStore>.Instance)
            .Start("job"));
        var (id, job) = MakeQueueEntry("a");
        _sut.Enqueue([(id, job)]);
        await _sut.ExecuteAllAsync();
        var logPath = _sut.Items.Single().LogPath;
        logPath.Should().NotBeNull();

        _sut.ResetToPending([id]).Single().LogPath.Should().BeNull();

        Directory.Delete(Path.GetDirectoryName(logPath!)!, recursive: true);
    }

    // --- Replace ---

    [Fact]
    public void Replace_PendingItem_SwapsJob()
    {
        var (id, original) = MakeQueueEntry("a");
        _sut.Enqueue([(id, original)]);
        var renamed = original with { Job = original.Job with { OutputPath = "/out/renamed.mkv" } };

        _sut.Replace(id, renamed).Should().BeTrue();

        _sut.Items.Single().QueuedJob.Should().BeSameAs(renamed);
    }

    [Fact]
    public void Replace_UnknownItem_ReturnsFalse()
    {
        var (_, job) = MakeQueueEntry();

        _sut.Replace("missing", job).Should().BeFalse();
    }

    [Fact]
    public async Task Replace_CompletedItem_ReturnsFalseAndKeepsJob()
    {
        var (id, original) = MakeQueueEntry("a");
        _sut.Enqueue([(id, original)]);
        await _sut.ExecuteAllAsync();

        _sut.Replace(id, original with { Job = original.Job with { OutputPath = "/out/late.mkv" } })
            .Should().BeFalse();

        _sut.Items.Single().QueuedJob.Should().BeSameAs(original);
    }

    // --- Enqueue ---

    [Fact]
    public void Enqueue_AddsItemsToList()
    {
        var entry = MakeQueueEntry();
        _sut.Enqueue([entry]);

        _sut.Items.Should().HaveCount(1);
        _sut.Items[0].Id.Should().Be(entry.Id);
        _sut.Items[0].State.Should().Be(JobState.Pending);
    }

    [Fact]
    public void Enqueue_MultipleItems_AllVisible()
    {
        _sut.Enqueue([MakeQueueEntry("a"), MakeQueueEntry("b"), MakeQueueEntry("c")]);
        _sut.Items.Should().HaveCount(3);
    }

    // --- Items ---

    [Fact]
    public void Items_ReturnsSnapshotNotReference()
    {
        _sut.Enqueue([MakeQueueEntry()]);
        var snapshot1 = _sut.Items;
        var snapshot2 = _sut.Items;

        snapshot1.Should().NotBeSameAs(snapshot2);
    }

    // --- HasPending ---

    [Fact]
    public void HasPending_WithPendingItems_ReturnsTrue()
    {
        _sut.Enqueue([MakeQueueEntry()]);
        _sut.HasPending.Should().BeTrue();
    }

    [Fact]
    public void HasPending_EmptyQueue_ReturnsFalse()
    {
        _sut.HasPending.Should().BeFalse();
    }

    // --- Remove ---

    [Fact]
    public void Remove_RemovesPendingItems()
    {
        _sut.Enqueue([MakeQueueEntry("a"), MakeQueueEntry("b")]);
        var remaining = _sut.Remove(["a"]);

        remaining.Should().HaveCount(1);
        remaining[0].Id.Should().Be("b");
    }

    [Fact]
    public void Remove_NonexistentId_NoEffect()
    {
        _sut.Enqueue([MakeQueueEntry("a")]);
        var remaining = _sut.Remove(["nonexistent"]);
        remaining.Should().HaveCount(1);
    }

    // --- ExecuteAllAsync ---

    [Fact]
    public async Task ExecuteAllAsync_RunsAllPendingItems()
    {
        _sut.Enqueue([MakeQueueEntry("a"), MakeQueueEntry("b")]);
        var result = await _sut.ExecuteAllAsync();

        result.Should().HaveCount(2);
        result.Should().OnlyContain(i => i.State == JobState.Completed);
    }

    [Fact]
    public async Task ExecuteAllAsync_InvokesItemStartedCallback()
    {
        _sut.Enqueue([MakeQueueEntry("a")]);
        var started = new List<string>();

        await _sut.ExecuteAllAsync(itemStarted: item => started.Add(item.Id));

        started.Should().ContainSingle().Which.Should().Be("a");
    }

    [Fact]
    public async Task ExecuteAllAsync_InvokesItemFinishedCallback()
    {
        _sut.Enqueue([MakeQueueEntry("a")]);
        var finished = new List<string>();

        await _sut.ExecuteAllAsync(itemFinished: item => finished.Add(item.Id));

        finished.Should().ContainSingle().Which.Should().Be("a");
    }

    [Fact]
    public async Task ExecuteAllAsync_AlreadyRunning_ThrowsInvalidOperation()
    {
        var tcs = new TaskCompletionSource<JobResult>();
        _mockExecution.ExecuteAsync(
            Arg.Any<JobDefinition>(),
            Arg.Any<SourceMediaInfo>(),
            Arg.Any<Action<JobProgress>?>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<ExecutionPauseState?>(),
            Arg.Any<CancellationToken>())
        .Returns(tcs.Task);

        _sut.Enqueue([MakeQueueEntry()]);
        var firstRun = _sut.ExecuteAllAsync();

        var act = () => _sut.ExecuteAllAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Unblock
        tcs.SetResult(new JobResult(
            new TestJobDefinitionBuilder().Build(),
            JobState.Completed, [], [], 1.0, ""));
        await firstRun;
    }

    [Fact]
    public async Task ExecuteAllAsync_CancellationStopsExecution()
    {
        _sut.Enqueue([MakeQueueEntry("a"), MakeQueueEntry("b")]);

        var callCount = 0;
        _mockExecution.ExecuteAsync(
            Arg.Any<JobDefinition>(),
            Arg.Any<SourceMediaInfo>(),
            Arg.Any<Action<JobProgress>?>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<ExecutionPauseState?>(),
            Arg.Any<CancellationToken>())
        .Returns(call =>
        {
            callCount++;
            var ct = call.Arg<CancellationToken>();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new JobResult(
                call.Arg<JobDefinition>(),
                JobState.Completed, [], [], 1.0, ""));
        });

        using var cts = new CancellationTokenSource();
        var finished = new List<string>();

        // Cancel after first item finishes
        var originalFinished = new Action<QueueItem>(item =>
        {
            finished.Add(item.Id);
            cts.Cancel();
        });

        var result = await _sut.ExecuteAllAsync(
            itemFinished: originalFinished,
            ct: cts.Token);

        // First item should complete, second should be canceled
        finished.Should().ContainSingle().Which.Should().Be("a");
    }

    // --- CancelActive ---

    [Fact]
    public void CancelActive_WhenNotRunning_SetsAllPendingToCanceled()
    {
        _sut.Enqueue([MakeQueueEntry("a"), MakeQueueEntry("b")]);
        _sut.CancelActive();

        _sut.Items.Should().OnlyContain(i => i.State == JobState.Canceled);
        _sut.HasPending.Should().BeFalse();
    }

    [Fact]
    public void CancelActive_SetsFailureMessage()
    {
        _sut.Enqueue([MakeQueueEntry("a")]);
        _sut.CancelActive();

        _sut.Items[0].FailureMessage.Should().NotBeNullOrEmpty();
    }

    // --- After cancel, can run again ---

    [Fact]
    public async Task ExecuteAllAsync_AfterCancel_CanRunAgain()
    {
        _sut.Enqueue([MakeQueueEntry("a")]);
        _sut.CancelActive();

        // Enqueue new items
        _sut.Enqueue([MakeQueueEntry("b")]);
        var result = await _sut.ExecuteAllAsync();

        result.Should().Contain(i => i.Id == "b" && i.State == JobState.Completed);
    }

    // --- Mid-run enqueue ---

    [Fact]
    public async Task ExecuteAllAsync_ItemEnqueuedMidRun_IsProcessed()
    {
        // Gate the first job so the loop is still running when we enqueue a second.
        var firstGate = new TaskCompletionSource<JobResult>();
        var finishedIds = new List<string>();

        _mockExecution.ExecuteAsync(
            Arg.Any<JobDefinition>(),
            Arg.Any<SourceMediaInfo>(),
            Arg.Any<Action<JobProgress>?>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<ExecutionPauseState?>(),
            Arg.Any<CancellationToken>())
        .Returns(call =>
        {
            // First invocation blocks until firstGate is set; subsequent invocations
            // complete synchronously.
            if (!firstGate.Task.IsCompleted)
                return firstGate.Task;

            return Task.FromResult(new JobResult(
                call.Arg<JobDefinition>(),
                JobState.Completed, [], [], 1.0, ""));
        });

        _sut.Enqueue([MakeQueueEntry("a")]);

        var run = _sut.ExecuteAllAsync(itemFinished: i => finishedIds.Add(i.Id));

        // Enqueue "b" while "a" is mid-flight, then release "a".
        _sut.Enqueue([MakeQueueEntry("b")]);
        firstGate.SetResult(new JobResult(
            new TestJobDefinitionBuilder().Build(),
            JobState.Completed, [], [], 1.0, ""));

        var result = await run;

        finishedIds.Should().BeEquivalentTo(["a", "b"], o => o.WithStrictOrdering());
        result.Should().OnlyContain(i => i.State == JobState.Completed);
    }

    // --- ResetToPending ---

    [Fact]
    public async Task ResetToPending_ResetsFailedItemToPending()
    {
        _sut.Enqueue([MakeQueueEntry("a")]);
        _mockExecution.ExecuteAsync(
            Arg.Any<JobDefinition>(),
            Arg.Any<SourceMediaInfo>(),
            Arg.Any<Action<JobProgress>?>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<ExecutionPauseState?>(),
            Arg.Any<CancellationToken>())
        .Returns<Task<JobResult>>(_ => throw new InvalidOperationException("boom"));

        Func<Task> run = () => _sut.ExecuteAllAsync();
        await run.Should().ThrowAsync<InvalidOperationException>();

        _sut.Items[0].State.Should().Be(JobState.Failed);

        SetupDefaultExecution();
        _sut.ResetToPending(["a"]);

        _sut.Items[0].State.Should().Be(JobState.Pending);
        _sut.Items[0].FailureMessage.Should().BeNull();
        _sut.Items[0].Result.Should().BeNull();
        _sut.Items[0].Progress.Should().BeNull();
    }

    [Fact]
    public async Task ResetToPending_LeavesRunningItemsUntouched()
    {
        var tcs = new TaskCompletionSource<JobResult>();
        _mockExecution.ExecuteAsync(
            Arg.Any<JobDefinition>(),
            Arg.Any<SourceMediaInfo>(),
            Arg.Any<Action<JobProgress>?>(),
            Arg.Any<Action<string>?>(),
            Arg.Any<ExecutionPauseState?>(),
            Arg.Any<CancellationToken>())
        .Returns(tcs.Task);

        _sut.Enqueue([MakeQueueEntry("a")]);
        var run = _sut.ExecuteAllAsync();

        // Wait for "a" to enter Running state.
        var spin = new System.Diagnostics.Stopwatch();
        spin.Start();
        while (_sut.Items[0].State != JobState.Running && spin.ElapsedMilliseconds < 1000)
            Thread.Sleep(10);

        _sut.ResetToPending(["a"]);
        _sut.Items[0].State.Should().Be(JobState.Running);

        tcs.SetResult(new JobResult(
            new TestJobDefinitionBuilder().Build(),
            JobState.Completed, [], [], 1.0, ""));
        await run;
    }

    [Fact]
    public void ResetToPending_EmptyIds_NoEffect()
    {
        _sut.Enqueue([MakeQueueEntry("a")]);
        var result = _sut.ResetToPending([]);
        result.Should().HaveCount(1);
        result[0].State.Should().Be(JobState.Pending);
    }

    // --- Empty queue ---

    [Fact]
    public async Task Pause_ControlsActiveExecutionAndBlocksNextJobUntilResume()
    {
        var first = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var pauses = 0;
        var resumes = 0;
        _mockExecution.ExecuteAsync(Arg.Any<JobDefinition>(), Arg.Any<SourceMediaInfo>(),
                Arg.Any<Action<JobProgress>?>(), Arg.Any<Action<string>?>(),
                Arg.Any<ExecutionPauseState?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls++;
                if (calls == 1)
                {
                    call.Arg<ExecutionPauseState>().StartProcess(() => { }, () => pauses++, () => resumes++);
                    return first.Task;
                }
                return Task.FromResult(new JobResult(call.Arg<JobDefinition>(), JobState.Completed, [], [], 0, ""));
            });
        var entry = MakeQueueEntry("a");
        _sut.Enqueue([entry, MakeQueueEntry("b")]);
        var run = _sut.ExecuteAllAsync(itemFinished: _ => finished.TrySetResult());
        _sut.Pause();
        _sut.IsPaused.Should().BeTrue();
        pauses.Should().Be(1);
        first.SetResult(new JobResult(entry.Job.Job, JobState.Completed, [], [], 0, ""));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        calls.Should().Be(1);
        _sut.Items[1].State.Should().Be(JobState.Pending);

        _sut.Resume();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        resumes.Should().Be(1);
        calls.Should().Be(2);
        _sut.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task CancelActive_WhilePausedBetweenJobs_UnblocksQueueAndAllowsNextRun()
    {
        _sut.Enqueue([MakeQueueEntry("a"), MakeQueueEntry("b")]);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = _sut.ExecuteAllAsync(itemFinished: _ =>
        {
            _sut.Pause();
            finished.TrySetResult();
        });
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _sut.CancelActive();
        Func<Task> wait = () => run.WaitAsync(TimeSpan.FromSeconds(5));
        await wait.Should().ThrowAsync<OperationCanceledException>();
        _sut.IsPaused.Should().BeFalse();
        _sut.Items[1].State.Should().Be(JobState.Canceled);

        _sut.Enqueue([MakeQueueEntry("c")]);
        var result = await _sut.ExecuteAllAsync();
        result.Should().Contain(item => item.Id == "c" && item.State == JobState.Completed);
    }

    [Fact]
    public void PauseAndResume_WhenIdle_DoNotAffectNextRun()
    {
        _sut.Pause();
        _sut.IsPaused.Should().BeFalse();
        _sut.Resume();
        _sut.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAllAsync_EmptyQueue_ReturnsEmpty()
    {
        var result = await _sut.ExecuteAllAsync();
        result.Should().BeEmpty();
    }
}
