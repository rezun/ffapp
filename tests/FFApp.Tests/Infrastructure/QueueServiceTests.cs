using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure;
using FFApp.Infrastructure.Logging;
using FFApp.Tests.Builders;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Core;

namespace FFApp.Tests.Infrastructure;

public sealed class QueueServiceTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private readonly IJobExecutionService _mockExecution = Substitute.For<IJobExecutionService>();
    private readonly IJobLogStore _jobLogs = Substitute.For<IJobLogStore>();
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), $"ffapp-queue-logs-{Guid.NewGuid():N}");
    private readonly QueueService _sut;

    public QueueServiceTests()
    {
        _jobLogs.Start(Arg.Any<string>()).Returns(_ => JobLog.Discarding);
        _sut = new QueueService(_ => _mockExecution, _jobLogs, TimeProvider.System);
        SetupExecution(call => Task.FromResult(Completed(call.Arg<JobDefinition>())));
    }

    public void Dispose()
    {
        if (Directory.Exists(_logDirectory))
            Directory.Delete(_logDirectory, recursive: true);
    }

    private void SetupExecution(Func<CallInfo, Task<JobResult>> execute) =>
        _mockExecution.ExecuteAsync(
                Arg.Any<JobDefinition>(), Arg.Any<SourceMediaInfo>(), Arg.Any<Action<JobProgress>?>(),
                Arg.Any<Action<string>?>(), Arg.Any<ExecutionPauseState?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(execute);

    /// <summary>Routes job logs to real files under a per-test directory that <see cref="Dispose"/> removes.</summary>
    private void UseFileLogs()
    {
        var store = new JobLogStore(
            new JobLogOptions(_logDirectory, TimeSpan.FromDays(30), "FFApp tests"),
            TimeProvider.System,
            NullLogger<JobLogStore>.Instance);
        _jobLogs.Start(Arg.Any<string>()).Returns(call => store.Start(call.Arg<string>()));
    }

    private static JobResult Completed(JobDefinition? job = null) =>
        new(job ?? new TestJobDefinitionBuilder().Build(), JobState.Completed, [], [], 1.0, "");

    private static (string Id, QueuedJob Job) MakeQueueEntry(string id)
    {
        var job = new TestJobDefinitionBuilder().Build();
        var media = new TestMediaInfoBuilder().Build();
        return (id, new QueuedJob(job, media, [], new ValidationResult()));
    }

    // --- Job logs ---

    [Fact]
    public async Task ExecuteAll_WritesEncoderOutputAndResultToTheJobLog()
    {
        UseFileLogs();
        SetupExecution(call =>
        {
            call.ArgAt<Action<string>?>(3)?.Invoke("frame=  42 fps=24");
            return Task.FromResult(new JobResult(call.Arg<JobDefinition>(), JobState.Completed, [], [], 83, ""));
        });
        var (id, job) = MakeQueueEntry("a");
        _sut.Enqueue([(id, job)]);
        var started = new List<QueueItem>();
        var uiLines = new List<string>();

        await _sut.ExecuteAllAsync(itemStarted: started.Add, logCallback: (_, line) => uiLines.Add(line));

        var logPath = _sut.Items.Single().LogPath;
        logPath.Should().NotBeNull().And.StartWith(_logDirectory);
        started.Single().LogPath.Should().Be(logPath, "the UI learns the path when the job starts");
        uiLines.Should().Equal("frame=  42 fps=24");
        var saved = await File.ReadAllTextAsync(logPath!);
        saved.Should().Contain($"Source:    {job.Job.SourcePath}")
            .And.Contain("frame=  42 fps=24")
            .And.Contain("Result:    Completed")
            .And.Contain("Elapsed:   00:01:23");
    }

    // --- Enqueue / Items / HasPending ---

    [Fact]
    public void Enqueue_AppendsPendingItemsWithoutChangingEarlierSnapshots()
    {
        var before = _sut.Items;
        _sut.HasPending.Should().BeFalse();

        var created = _sut.Enqueue([MakeQueueEntry("a"), MakeQueueEntry("b")]);

        created.Select(i => i.Id).Should().Equal("a", "b");
        _sut.Items.Select(i => (i.Id, i.State)).Should().Equal(("a", JobState.Pending), ("b", JobState.Pending));
        _sut.HasPending.Should().BeTrue();
        before.Should().BeEmpty("Items returns a snapshot, not the live list");
    }

    // --- Replace ---

    [Fact]
    public void Replace_SwapsJobOfPendingItem_AndRejectsUnknownId()
    {
        var (id, original) = MakeQueueEntry("a");
        _sut.Enqueue([(id, original)]);
        var renamed = original with { Job = original.Job with { OutputPath = "/out/renamed.mkv" } };

        _sut.Replace("missing", renamed).Should().BeFalse();
        _sut.Replace(id, renamed).Should().BeTrue();

        _sut.Items.Single().QueuedJob.Should().BeSameAs(renamed);
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

    // --- Remove ---

    [Fact]
    public void Remove_RemovesOnlyTheRequestedIds()
    {
        _sut.Enqueue([MakeQueueEntry("a"), MakeQueueEntry("b")]);

        var remaining = _sut.Remove(["a", "nonexistent"]);

        remaining.Select(i => i.Id).Should().Equal("b");
    }

    // --- Running item ---

    [Fact]
    public async Task RunningItem_IsNotReset_Removed_Replaced_OrRunTwice()
    {
        var release = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupExecution(_ => release.Task);
        var (id, job) = MakeQueueEntry("a");
        _sut.Enqueue([(id, job)]);
        var run = _sut.ExecuteAllAsync(itemStarted: _ => started.TrySetResult());
        await started.Task.WaitAsync(WaitLimit);

        _sut.ResetToPending([id]).Single().State.Should().Be(JobState.Running);
        _sut.Remove([id]).Single().State.Should().Be(JobState.Running);
        _sut.Replace(id, job with { Job = job.Job with { OutputPath = "/out/late.mkv" } }).Should().BeFalse();
        var secondRun = () => _sut.ExecuteAllAsync();
        await secondRun.Should().ThrowAsync<InvalidOperationException>();

        release.SetResult(Completed());
        (await run.WaitAsync(WaitLimit)).Single().State.Should().Be(JobState.Completed);
    }

    // --- ExecuteAllAsync ---

    [Theory]
    [InlineData(false, JobState.Completed)]
    [InlineData(true, JobState.Canceled)]
    public async Task ExecuteAllAsync_TokenCanceled_StopsAndLeavesLaterItemsPending(
        bool cancelDuringExecution, JobState expectedFirstState)
    {
        SetupExecution(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Task.FromResult(Completed(call.Arg<JobDefinition>()));
        });
        _sut.Enqueue([MakeQueueEntry("a"), MakeQueueEntry("b")]);
        using var cts = new CancellationTokenSource();

        var result = await _sut.ExecuteAllAsync(
            itemStarted: _ => { if (cancelDuringExecution) cts.Cancel(); },
            itemFinished: _ => cts.Cancel(),
            ct: cts.Token);

        result.Select(i => i.State).Should().Equal(expectedFirstState, JobState.Pending);
    }

    [Fact]
    public async Task ExecuteAllAsync_ItemEnqueuedMidRun_IsProcessed()
    {
        // Gate the first job so the loop is still running when we enqueue a second.
        var firstGate = new TaskCompletionSource<JobResult>();
        var finishedIds = new List<string>();
        SetupExecution(call => firstGate.Task.IsCompleted
            ? Task.FromResult(Completed(call.Arg<JobDefinition>()))
            : firstGate.Task);
        _sut.Enqueue([MakeQueueEntry("a")]);

        var run = _sut.ExecuteAllAsync(itemFinished: i => finishedIds.Add(i.Id));

        // Enqueue "b" while "a" is mid-flight, then release "a".
        _sut.Enqueue([MakeQueueEntry("b")]);
        firstGate.SetResult(Completed());

        var result = await run;

        finishedIds.Should().Equal("a", "b");
        result.Should().OnlyContain(i => i.State == JobState.Completed);
    }

    // --- CancelActive ---

    [Fact]
    public void CancelActive_WhenNotRunning_CancelsAllPendingItemsWithAReason()
    {
        _sut.Enqueue([MakeQueueEntry("a"), MakeQueueEntry("b")]);

        _sut.CancelActive();

        _sut.Items.Should().OnlyContain(i => i.State == JobState.Canceled && !string.IsNullOrEmpty(i.FailureMessage));
        _sut.HasPending.Should().BeFalse();
    }

    // --- ResetToPending ---

    [Fact]
    public async Task ResetToPending_ClearsOutcomeOfCompletedAndFailedItems()
    {
        UseFileLogs();
        _mockExecution.ExecuteAsync(
                Arg.Any<JobDefinition>(), Arg.Any<SourceMediaInfo>(), Arg.Any<Action<JobProgress>?>(),
                Arg.Any<Action<string>?>(), Arg.Any<ExecutionPauseState?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(Completed()),
                Task.FromException<JobResult>(new InvalidOperationException("boom")));
        _sut.Enqueue([MakeQueueEntry("a"), MakeQueueEntry("b")]);
        var run = () => _sut.ExecuteAllAsync();
        await run.Should().ThrowAsync<InvalidOperationException>();
        _sut.Items[0].Should().Match<QueueItem>(i => i.State == JobState.Completed
            && i.Result != null && i.Progress != null && i.LogPath != null);
        _sut.Items[1].Should().Match<QueueItem>(i => i.State == JobState.Failed
            && i.FailureMessage == "boom" && i.LogPath != null);

        var reset = _sut.ResetToPending(["a", "b"]);

        reset.Should().OnlyContain(i => i.State == JobState.Pending
            && i.Result == null && i.Progress == null && i.FailureMessage == null && i.LogPath == null);
    }

    // --- Pause ---

    [Fact]
    public async Task Pause_ControlsActiveExecutionAndBlocksNextJobUntilResume()
    {
        var first = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var pauses = 0;
        var resumes = 0;
        SetupExecution(call =>
        {
            calls++;
            if (calls == 1)
            {
                call.Arg<ExecutionPauseState>().StartProcess(() => { }, () => pauses++, () => resumes++);
                return first.Task;
            }
            return Task.FromResult(Completed(call.Arg<JobDefinition>()));
        });
        _sut.Enqueue([MakeQueueEntry("a"), MakeQueueEntry("b")]);
        var run = _sut.ExecuteAllAsync(itemFinished: _ => finished.TrySetResult());
        _sut.Pause();
        _sut.IsPaused.Should().BeTrue();
        pauses.Should().Be(1);
        first.SetResult(Completed());
        await finished.Task.WaitAsync(WaitLimit);
        calls.Should().Be(1);
        _sut.Items[1].State.Should().Be(JobState.Pending);

        _sut.Resume();
        await run.WaitAsync(WaitLimit);
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
        await finished.Task.WaitAsync(WaitLimit);
        _sut.CancelActive();
        var wait = () => run.WaitAsync(WaitLimit);
        await wait.Should().ThrowAsync<OperationCanceledException>();
        _sut.IsPaused.Should().BeFalse();
        _sut.Items.Select(i => i.State).Should().Equal(JobState.Completed, JobState.Canceled);

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
}
