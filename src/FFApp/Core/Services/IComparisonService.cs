using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IComparisonService
{
    Task<ComparisonSession> LoadAsync(JobContext context, CancellationToken ct = default);
    Task SaveAsync(ComparisonSession session, CancellationToken ct = default);
    string ClipPath(string cacheKey);
    string CacheKey(ComparisonSession session, SourceMediaInfo media, ComparisonSample sample, ComparisonVariant variant);

    /// <summary>
    /// Encodes one clip. Cancellation removes partial output; only completed clips are
    /// published to the cache. Completed clips remain reusable if the batch is cancelled.
    /// </summary>
    Task<ComparisonClip> EncodeAsync(ComparisonSession session, SourceMediaInfo media,
        ComparisonSample sample, ComparisonVariant variant,
        Action<JobProgress>? progress = null, CancellationToken ct = default);
}
