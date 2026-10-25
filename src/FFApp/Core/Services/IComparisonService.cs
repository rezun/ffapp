using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IComparisonService
{
    Task<ComparisonSession> LoadAsync(JobContext context, CancellationToken ct = default);
    Task SaveAsync(ComparisonSession session, CancellationToken ct = default);
    string ClipPath(string cacheKey);
    string CacheKey(ComparisonSession session, SourceMediaInfo media, ComparisonSample sample, ComparisonVariant variant);
    ComparisonCacheUsage GetCacheUsage(string? sourcePath = null);
    void ProtectSession(ComparisonSession session);
    void ReleaseSession(string sourcePath);
    void SetCacheLimit(long bytes);
    long ClearClips(string? sourcePath = null);
    void DeleteComparison(string sourcePath);

    /// <summary>
    /// Encodes one clip. Cancellation removes partial output; only completed clips are
    /// published to the cache. Completed clips remain reusable if the batch is cancelled.
    /// </summary>
    Task<ComparisonClip> EncodeAsync(ComparisonSession session, SourceMediaInfo media,
        ComparisonSample sample, ComparisonVariant variant,
        Action<JobProgress>? progress = null, CancellationToken ct = default);
}
