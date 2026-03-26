using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IQueueService
{
    IReadOnlyList<QueueItem> Enqueue(IEnumerable<(string Id, QueuedJob Job)> jobs);
    IReadOnlyList<QueueItem> Items { get; }
    bool HasPending { get; }
    IReadOnlyList<QueueItem> Remove(IEnumerable<string> itemIds);
    Task<IReadOnlyList<QueueItem>> ExecuteAllAsync(
        Action<QueueItem>? itemStarted = null,
        Action<QueueItem>? itemProgress = null,
        Action<QueueItem>? itemFinished = null,
        Action<QueueItem, string>? logCallback = null,
        CancellationToken ct = default);
}
