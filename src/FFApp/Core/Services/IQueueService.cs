using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IQueueService
{
    IReadOnlyList<QueueItem> Enqueue(IEnumerable<(string Id, QueuedJob Job)> jobs);
    IReadOnlyList<QueueItem> Items { get; }
    bool HasPending { get; }
    bool IsPaused { get; }
    IReadOnlyList<QueueItem> Remove(IEnumerable<string> itemIds);
    /// <summary>
    /// Resets the specified items back to <see cref="JobState.Pending"/>, clearing any prior
    /// result/failure info. Running items are left untouched. Used when the user re-runs
    /// previously failed or canceled jobs without removing them from the queue first.
    /// </summary>
    IReadOnlyList<QueueItem> ResetToPending(IEnumerable<string> itemIds);
    Task<IReadOnlyList<QueueItem>> ExecuteAllAsync(
        Action<QueueItem>? itemStarted = null,
        Action<QueueItem>? itemProgress = null,
        Action<QueueItem>? itemFinished = null,
        Action<QueueItem, string>? logCallback = null,
        CancellationToken ct = default);
    void CancelActive();
    void Pause();
    void Resume();
}
