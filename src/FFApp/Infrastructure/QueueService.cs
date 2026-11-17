using System.Globalization;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure.Logging;

namespace FFApp.Infrastructure;

/// <summary>
/// Runs queued jobs in two lanes at the same time. The local lane encodes one job after another
/// on this computer. The server lane uploads one job after another; as soon as a job reports
/// that its upload finished, the next upload starts while the earlier jobs wait for, run on, or
/// download from the server agent.
/// </summary>
public sealed class QueueService(
    Func<QueuedJob, IJobExecutionService> executionServiceFactory,
    IJobLogStore jobLogs,
    TimeProvider timeProvider) : IQueueService, IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<QueueItem> _items = [];
    private readonly Dictionary<string, ActiveItem> _active = new(StringComparer.Ordinal);
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _activeCts;
    private bool _running;
    private bool _paused;
    private ExecutionPauseState? _localPause;
    private string? _localSlot;
    private string? _serverSlot;

    private sealed record ActiveItem(CancellationTokenSource Cancellation, ExecutionPauseState Pause, bool IsRemote);

    public bool IsPaused
    {
        get { lock (_lock) return _running && _paused; }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!_running)
                return;
            _paused = true;
            _localPause?.Pause();
            foreach (var active in _active.Values.Where(a => a.IsRemote))
                active.Pause.Pause();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (!_running)
                return;
            _paused = false;
            _localPause?.Resume();
            foreach (var active in _active.Values.Where(a => a.IsRemote))
                active.Pause.Resume();
        }
        Signal();
    }

    public bool PauseItem(string itemId)
    {
        lock (_lock)
        {
            if (!_active.TryGetValue(itemId, out var active))
                return false;
            active.Pause.Pause();
            return true;
        }
    }

    public bool ResumeItem(string itemId)
    {
        lock (_lock)
        {
            if (!_active.TryGetValue(itemId, out var active))
                return false;
            active.Pause.Resume();
        }
        Signal();
        return true;
    }

    public bool IsItemPaused(string itemId)
    {
        lock (_lock)
            return _active.TryGetValue(itemId, out var active) && active.Pause.IsPaused;
    }

    public bool CancelItem(string itemId)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(itemId, out var active))
            {
                active.Pause.Resume();
                active.Cancellation.Cancel();
                return true;
            }
            var index = _items.FindIndex(i => string.Equals(i.Id, itemId, StringComparison.Ordinal));
            if (index < 0 || _items[index].State != JobState.Pending)
                return false;
            _items[index] = _items[index] with
            {
                State = JobState.Canceled,
                FailureMessage = "Canceled before execution started.",
            };
        }
        Signal();
        return true;
    }

    public IReadOnlyList<QueueItem> Enqueue(IEnumerable<(string Id, QueuedJob Job)> jobs)
    {
        var created = new List<QueueItem>();
        lock (_lock)
        {
            foreach (var (id, job) in jobs)
            {
                var item = new QueueItem(id, job);
                _items.Add(item);
                created.Add(item);
            }
        }
        Signal();
        return created;
    }

    public IReadOnlyList<QueueItem> Items
    {
        get
        {
            lock (_lock)
                return [.. _items];
        }
    }

    public bool HasPending
    {
        get
        {
            lock (_lock)
                return _items.Any(i => i.State == JobState.Pending);
        }
    }

    public IReadOnlyList<QueueItem> Remove(IEnumerable<string> itemIds)
    {
        var removable = new HashSet<string>(itemIds);
        if (removable.Count == 0)
            return Items;

        lock (_lock)
        {
            _items.RemoveAll(i => removable.Contains(i.Id) && i.State != JobState.Running);
            return [.. _items];
        }
    }

    public IReadOnlyList<QueueItem> ResetToPending(IEnumerable<string> itemIds)
    {
        var toReset = new HashSet<string>(itemIds);
        if (toReset.Count == 0)
            return Items;

        IReadOnlyList<QueueItem> result;
        lock (_lock)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (!toReset.Contains(_items[i].Id))
                    continue;
                if (_items[i].State == JobState.Running)
                    continue;

                _items[i] = _items[i] with
                {
                    State = JobState.Pending,
                    Progress = null,
                    Result = null,
                    FailureMessage = null,
                    LogPath = null,
                };
            }
            result = [.. _items];
        }
        Signal();
        return result;
    }

    public bool Replace(string itemId, QueuedJob job)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        ArgumentNullException.ThrowIfNull(job);

        lock (_lock)
        {
            var index = _items.FindIndex(i => string.Equals(i.Id, itemId, StringComparison.Ordinal));
            if (index < 0 || _items[index].State is JobState.Running or JobState.Completed)
                return false;

            _items[index] = _items[index] with { QueuedJob = job };
            return true;
        }
    }

    /// <summary>
    /// Runs pending jobs of both lanes until none is left. Canceling <paramref name="ct"/> stops
    /// starting jobs and cancels the running ones; when a job throws, no further job starts and
    /// the exception is rethrown once the running jobs have finished.
    /// </summary>
    public async Task<IReadOnlyList<QueueItem>> ExecuteAllAsync(
        Action<QueueItem>? itemStarted = null,
        Action<QueueItem>? itemProgress = null,
        Action<QueueItem>? itemFinished = null,
        Action<QueueItem, string>? logCallback = null,
        CancellationToken ct = default)
    {
        CancellationToken runToken;
        ExecutionPauseState localPause;
        lock (_lock)
        {
            if (_running)
                throw new InvalidOperationException("Queue execution is already in progress.");
            _running = true;
            _paused = false;
            _localPause = localPause = new ExecutionPauseState(timeProvider);
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            runToken = _activeCts.Token;
        }

        var callbacks = new Callbacks(itemStarted, itemProgress, itemFinished, logCallback);
        var tasks = new Dictionary<string, Task>(StringComparer.Ordinal);
        Exception? failure = null;
        try
        {
            using var registration = runToken.Register(Signal);
            while (true)
            {
                var wake = WakeTask();

                foreach (var (id, task) in tasks.Where(entry => entry.Value.IsCompleted).ToList())
                {
                    tasks.Remove(id);
                    if (task.Exception?.InnerException is { } error)
                        failure ??= error;
                }

                if (runToken.IsCancellationRequested)
                {
                    if (tasks.Count == 0)
                        break;
                    await Task.WhenAny(tasks.Values).ConfigureAwait(false);
                    continue;
                }

                if (failure is null)
                    StartReadyItems(tasks, localPause, callbacks, runToken);

                if (tasks.Count == 0)
                {
                    if (failure is not null || !IsPaused || !HasPending)
                        break;
                    // Paused between jobs: wait for Resume or cancellation.
                    await wake.WaitAsync(runToken).ConfigureAwait(false);
                    continue;
                }

                await Task.WhenAny(Task.WhenAny(tasks.Values), wake).ConfigureAwait(false);
            }

            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
        finally
        {
            lock (_lock)
            {
                _paused = false;
                _localPause = null;
                _localSlot = null;
                _serverSlot = null;
                _activeCts?.Dispose();
                _activeCts = null;
                _running = false;
            }
        }

        return Items;
    }

    public void CancelActive()
    {
        lock (_lock)
        {
            _activeCts?.Cancel();
            foreach (var active in _active.Values)
                active.Pause.Resume();

            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].State == JobState.Pending)
                {
                    _items[i] = _items[i] with
                    {
                        State = JobState.Canceled,
                        FailureMessage = "Canceled before execution started.",
                    };
                }
            }
        }
        Signal();
    }

    // --- Lanes ---

    private sealed record Callbacks(
        Action<QueueItem>? Started,
        Action<QueueItem>? Progress,
        Action<QueueItem>? Finished,
        Action<QueueItem, string>? Log);

    /// <summary>Starts the next local job and the next server upload when their lane is free.</summary>
    private void StartReadyItems(Dictionary<string, Task> tasks, ExecutionPauseState localPause, Callbacks callbacks, CancellationToken runToken)
    {
        QueueItem? local = null, remote = null;
        ActiveItem? localActive = null, remoteActive = null;
        lock (_lock)
        {
            if (_paused)
                return;
            if (_localSlot is null && StartNextItem(remote: false) is { } nextLocal)
            {
                local = nextLocal;
                _localSlot = local.Id;
                localActive = new ActiveItem(CancellationTokenSource.CreateLinkedTokenSource(runToken), localPause, false);
                _active[local.Id] = localActive;
            }
            if (_serverSlot is null && StartNextItem(remote: true) is { } nextRemote)
            {
                remote = nextRemote;
                _serverSlot = remote.Id;
                remoteActive = new ActiveItem(CancellationTokenSource.CreateLinkedTokenSource(runToken),
                    new ExecutionPauseState(timeProvider), true);
                _active[remote.Id] = remoteActive;
            }
        }
        if (local is not null && localActive is not null)
            tasks[local.Id] = RunItemAsync(local, localActive, callbacks);
        if (remote is not null && remoteActive is not null)
            tasks[remote.Id] = RunItemAsync(remote, remoteActive, callbacks);
    }

    /// <summary>
    /// Executes one job. A server job frees its lane as soon as it reports that its upload is
    /// done, so the next upload can start.
    /// </summary>
    private async Task RunItemAsync(QueueItem item, ActiveItem active, Callbacks callbacks)
    {
        var itemId = item.Id;
        try
        {
            using var jobLog = StartJobLog(item.QueuedJob);
            item = UpdateItem(itemId, i => i with { LogPath = jobLog.FilePath }) ?? item;
            callbacks.Started?.Invoke(item);

            var executionService = executionServiceFactory(item.QueuedJob);

            void OnProgress(JobProgress progress)
            {
                if (active.IsRemote && progress.Remote is { Phase: not RemotePhase.Uploading })
                    ReleaseServerSlot(itemId);
                var updated = UpdateItem(itemId, i => i with
                {
                    State = JobState.Running,
                    Progress = progress,
                });
                if (updated is not null)
                    callbacks.Progress?.Invoke(updated);
            }

            void LogLine(string line)
            {
                jobLog.WriteLine(line);
                if (callbacks.Log is not null && FindItem(itemId) is { } current)
                    callbacks.Log(current, line);
            }

            try
            {
                var result = await executionService.ExecuteAsync(
                    item.QueuedJob.Job,
                    item.QueuedJob.MediaInfo,
                    OnProgress,
                    LogLine,
                    active.Pause,
                    ct: active.Cancellation.Token).ConfigureAwait(false);

                var finished = FinalizeItem(itemId, result.State, result);
                WriteJobLogResult(jobLog, finished);
                callbacks.Finished?.Invoke(finished);
            }
            catch (OperationCanceledException)
            {
                var canceled = FinalizeItem(itemId, JobState.Canceled,
                    failureMessage: "Canceled during execution.");
                WriteJobLogResult(jobLog, canceled);
                callbacks.Finished?.Invoke(canceled);
            }
            catch (Exception ex)
            {
                var failed = FinalizeItem(itemId, JobState.Failed,
                    failureMessage: ex.Message);
                jobLog.WriteLine(ex.ToString());
                WriteJobLogResult(jobLog, failed);
                callbacks.Finished?.Invoke(failed);
                throw;
            }
        }
        finally
        {
            lock (_lock)
            {
                _active.Remove(itemId);
                if (string.Equals(_localSlot, itemId, StringComparison.Ordinal))
                    _localSlot = null;
                if (string.Equals(_serverSlot, itemId, StringComparison.Ordinal))
                    _serverSlot = null;
            }
            active.Cancellation.Dispose();
            Signal();
        }
    }

    private void ReleaseServerSlot(string itemId)
    {
        lock (_lock)
        {
            if (!string.Equals(_serverSlot, itemId, StringComparison.Ordinal))
                return;
            _serverSlot = null;
        }
        Signal();
    }

    /// <summary>Wakes the coordinator. Taken before it inspects state, so no signal is missed.</summary>
    private Task WakeTask()
    {
        lock (_lock)
        {
            if (_wake.Task.IsCompleted)
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _wake.Task;
        }
    }

    private void Signal()
    {
        lock (_lock)
            _wake.TrySetResult();
    }

    // --- Private helpers ---

    private JobLog StartJobLog(QueuedJob queuedJob)
    {
        var job = queuedJob.Job;
        var log = jobLogs.Start(Path.GetFileName(job.OutputPath));
        log.WriteField("Source", job.SourcePath);
        log.WriteField("Output", job.OutputPath);
        log.WriteLine("");
        foreach (var command in queuedJob.Commands)
            log.WriteLine($"$ {command.DisplayString}");
        log.WriteLine("");
        return log;
    }

    private static void WriteJobLogResult(JobLog log, QueueItem item)
    {
        log.WriteLine("");
        log.WriteField("Result", item.State.ToString());
        if (item.Result is { } result)
            log.WriteField("Elapsed", TimeSpan.FromSeconds(result.ElapsedSeconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        if (item.FailureMessage is { Length: > 0 } message)
            log.WriteField("Reason", message);
    }

    private QueueItem? StartNextItem(bool remote)
    {
        lock (_lock)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].State != JobState.Pending || _items[i].QueuedJob.IsRemote != remote)
                    continue;

                var running = _items[i] with { State = JobState.Running };
                _items[i] = running;
                return running;
            }
        }
        return null;
    }

    private QueueItem FinalizeItem(
        string itemId,
        JobState state,
        JobResult? result = null,
        string? failureMessage = null)
    {
        var updated = UpdateItem(itemId, item => item with
        {
            State = state,
            Result = result,
            FailureMessage = failureMessage,
            Progress = item.Progress ?? TerminalProgress(result),
        });
        return updated ?? throw new InvalidOperationException(
            $"Queue item {itemId} no longer exists.");
    }

    private QueueItem? UpdateItem(string itemId, Func<QueueItem, QueueItem> transform)
    {
        lock (_lock)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].Id != itemId)
                    continue;

                var updated = transform(_items[i]);
                _items[i] = updated;
                return updated;
            }
        }
        return null;
    }

    private QueueItem? FindItem(string itemId)
    {
        lock (_lock)
        {
            foreach (var item in _items)
            {
                if (item.Id == itemId)
                    return item;
            }
        }
        return null;
    }

    private static JobProgress? TerminalProgress(JobResult? result)
    {
        if (result is null)
            return null;

        var passCount = result.Commands.Count(command => command.Protocol == ProcessProtocol.Ffmpeg);
        var completedPasses = result.ProcessResults.Count(
            processResult => processResult.Command.Protocol == ProcessProtocol.Ffmpeg);

        return new JobProgress(
            State: result.State,
            PassIndex: completedPasses > 0 ? completedPasses : 1,
            PassCount: passCount,
            ElapsedSeconds: result.ElapsedSeconds,
            Percentage: result.State == JobState.Completed ? 100.0 : null);
    }

    public void Dispose()
    {
        CancelActive();
    }
}
