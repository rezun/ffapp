using System.Globalization;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure.Logging;

namespace FFApp.Infrastructure;

public sealed class QueueService(
    Func<QueuedJob, IJobExecutionService> executionServiceFactory,
    IJobLogStore jobLogs,
    TimeProvider timeProvider) : IQueueService, IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<QueueItem> _items = [];
    private CancellationTokenSource? _activeCts;
    private bool _running;
    private ExecutionPauseState? _pauseState;

    public bool IsPaused
    {
        get { lock (_lock) return _pauseState?.IsPaused ?? false; }
    }

    public void Pause()
    {
        lock (_lock)
            _pauseState?.Pause();
    }

    public void Resume()
    {
        lock (_lock)
            _pauseState?.Resume();
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
            return [.. _items];
        }
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

    public async Task<IReadOnlyList<QueueItem>> ExecuteAllAsync(
        Action<QueueItem>? itemStarted = null,
        Action<QueueItem>? itemProgress = null,
        Action<QueueItem>? itemFinished = null,
        Action<QueueItem, string>? logCallback = null,
        CancellationToken ct = default)
    {
        ExecutionPauseState pauseState;
        CancellationToken linkedToken;
        lock (_lock)
        {
            if (_running)
                throw new InvalidOperationException("Queue execution is already in progress.");
            _running = true;
            _pauseState = pauseState = new ExecutionPauseState(timeProvider);
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedToken = _activeCts.Token;
        }

        try
        {
            while (true)
            {
                await pauseState.WaitWhilePausedAsync(linkedToken).ConfigureAwait(false);

                var item = StartNextItem();
                if (item is null)
                    break;

                var itemId = item.Id;
                using var jobLog = StartJobLog(item.QueuedJob);
                item = UpdateItem(itemId, i => i with { LogPath = jobLog.FilePath }) ?? item;
                itemStarted?.Invoke(item);

                var executionService = executionServiceFactory(item.QueuedJob);

                Action<JobProgress>? progressCb = itemProgress is not null
                    ? progress =>
                    {
                        var updated = UpdateItem(itemId, i => i with
                        {
                            State = JobState.Running,
                            Progress = progress,
                        });
                        if (updated is not null)
                            itemProgress(updated);
                    }
                : null;

                void LogLine(string line)
                {
                    jobLog.WriteLine(line);
                    if (logCallback is not null && FindItem(itemId) is { } current)
                        logCallback(current, line);
                }

                try
                {
                    var result = await executionService.ExecuteAsync(
                        item.QueuedJob.Job,
                        item.QueuedJob.MediaInfo,
                        progressCb,
                        LogLine,
                        pauseState,
                        linkedToken).ConfigureAwait(false);

                    var finished = FinalizeItem(itemId, result.State, result);
                    WriteJobLogResult(jobLog, finished);
                    itemFinished?.Invoke(finished);
                }
                catch (OperationCanceledException)
                {
                    var canceled = FinalizeItem(itemId, JobState.Canceled,
                        failureMessage: "Canceled during execution.");
                    WriteJobLogResult(jobLog, canceled);
                    itemFinished?.Invoke(canceled);
                    break;
                }
                catch (Exception ex)
                {
                    var failed = FinalizeItem(itemId, JobState.Failed,
                        failureMessage: ex.Message);
                    jobLog.WriteLine(ex.ToString());
                    WriteJobLogResult(jobLog, failed);
                    itemFinished?.Invoke(failed);
                    throw;
                }

                if (linkedToken.IsCancellationRequested)
                    break;
            }
        }
        finally
        {
            lock (_lock)
            {
                _pauseState = null;
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

    private QueueItem? StartNextItem()
    {
        lock (_lock)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].State != JobState.Pending)
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
