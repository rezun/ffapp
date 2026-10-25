using System.Text.Json;
using FFApp.Core.Models;

namespace FFApp.Infrastructure;

public sealed partial class ComparisonService
{
    public static readonly long DefaultCacheLimitBytes = 5L * 1024 * 1024 * 1024;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, HashSet<string>> _protectedSessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _cacheLimit = DefaultCacheLimitBytes;

    private FileInfo[] CachedClips() => Directory.Exists(Path.Combine(_directory, "clips"))
        ? new DirectoryInfo(Path.Combine(_directory, "clips")).GetFiles()
            .Where(f => IsClipKey(f.Name) && !f.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToArray()
        : [];

    private static bool IsClipKey(string key) => key.Length is 68 or 69
        && Path.GetExtension(key) is ".mp4" or ".mkv" or ".webm"
        && Path.GetFileNameWithoutExtension(key).Length == 64
        && Path.GetFileNameWithoutExtension(key).All(char.IsAsciiHexDigit);

    private HashSet<string> SessionKeys(string sourcePath)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (_protectedSessions.TryGetValue(sourcePath, out var active)) keys.UnionWith(active);
        var manifest = SessionPath(sourcePath);
        if (File.Exists(manifest))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            foreach (var result in document.RootElement.GetProperty("results").EnumerateArray())
            {
                if (result.GetProperty("CacheKey").GetString() is { } key && IsClipKey(key)) keys.Add(key);
            }
        }
        return keys;
    }

    public ComparisonCacheUsage GetCacheUsage(string? sourcePath = null)
    {
        lock (_cacheLock)
        {
            var files = CachedClips();
            var owned = sourcePath is null ? [] : SessionKeys(sourcePath);
            var protectedKeys = ProtectedKeys();
            return new(files.Sum(f => f.Length), files.Where(f => owned.Contains(f.Name)).Sum(f => f.Length),
                files.Where(f => protectedKeys.Contains(f.Name)).Sum(f => f.Length));
        }
    }

    public void ProtectSession(ComparisonSession session)
    {
        lock (_cacheLock)
        {
            var keys = SessionKeys(session.SourcePath);
            keys.UnionWith(session.Results.Select(r => r.CacheKey));
            _protectedSessions[session.SourcePath] = keys;
            foreach (var key in keys.Where(IsClipKey)) TouchClip(ClipPath(key));
        }
    }

    public void ReleaseSession(string sourcePath)
    {
        lock (_cacheLock)
        {
            if (_protectedSessions.TryGetValue(sourcePath, out var keys))
                foreach (var key in keys.Where(IsClipKey)) TouchClip(ClipPath(key));
            _protectedSessions.Remove(sourcePath);
            TrimCache();
        }
    }

    public void SetCacheLimit(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_cacheLock)
        {
            _cacheLimit = bytes;
            TrimCache();
        }
    }

    // Each deletion is independently safe. A failure leaves the remaining clips reusable;
    // manifests and original media are never changed by cache cleanup.
    public long ClearClips(string? sourcePath = null)
    {
        lock (_cacheLock)
        {
            var owned = sourcePath is null ? null : SessionKeys(sourcePath);
            var protectedKeys = ProtectedKeys(sourcePath);
            long removed = 0;
            foreach (var file in CachedClips().Where(f => (owned is null || owned.Contains(f.Name))
                         && !protectedKeys.Contains(f.Name)))
            {
                var size = file.Length;
                file.Delete();
                removed += size;
            }
            return removed;
        }
    }

    // Delete clips first: if cleanup fails, the manifest is retained so retrying is safe.
    public void DeleteComparison(string sourcePath)
    {
        lock (_cacheLock)
        {
            ClearClips(sourcePath);
            File.Delete(SessionPath(sourcePath));
        }
    }

    private HashSet<string> ProtectedKeys(string? exceptSource = null) => new(
        _protectedSessions.Where(p => !string.Equals(p.Key, exceptSource, StringComparison.Ordinal))
            .SelectMany(p => p.Value), StringComparer.Ordinal);

    private void TouchClip(string path)
    {
        if (File.Exists(path)) File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
    }

    private void TrimCache()
    {
        var files = CachedClips();
        var total = files.Sum(f => f.Length);
        var protectedKeys = ProtectedKeys();
        foreach (var file in files.Where(f => !protectedKeys.Contains(f.Name)).OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= _cacheLimit) break;
            var size = file.Length;
            file.Delete();
            total -= size;
        }
    }
}
