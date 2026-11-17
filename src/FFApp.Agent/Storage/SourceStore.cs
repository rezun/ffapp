using System.Collections.Concurrent;
using System.Security.Cryptography;

using FFApp.Remote.Contracts;

namespace FFApp.Agent.Storage;

/// <summary>Persisted description of an uploaded source (<c>sources/&lt;id&gt;/meta.json</c>).</summary>
public sealed record SourceRecord(
    string SourceId,
    string ClientId,
    string Fingerprint,
    long Size,
    string FileName,
    string Extension,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    bool Complete = false,
    string? Sha256 = null);

/// <summary>
/// Uploaded sources. Uploads are resumable: data is appended to <c>source.&lt;ext&gt;.partial</c>
/// at the offset the agent has confirmed, and the SHA-256 is checked before the source is used.
/// Sources are deduplicated by fingerprint within one client only, so a client cannot learn which
/// files other clients uploaded.
/// </summary>
public sealed class SourceStore(AgentPaths paths, TimeProvider timeProvider, ILogger<SourceStore> logger)
{
    private static readonly int CopyBufferSize = 1024 * 1024;
    private static readonly int MaxExtensionLength = 10;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, SourceRecord> _sources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new(StringComparer.Ordinal);

    public void Load()
    {
        paths.EnsureCreated();
        lock (_lock)
        {
            _sources.Clear();
            foreach (var directory in Directory.EnumerateDirectories(paths.Sources))
            {
                var id = Path.GetFileName(directory);
                try
                {
                    if (AtomicFile.ReadJson(paths.SourceMeta(id), AgentJsonContext.Default.SourceRecord) is { } record)
                        _sources[id] = record;
                    else
                        Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    logger.LogError(ex, "Skipping unreadable source {SourceId}", id);
                }
            }
        }
    }

    public IReadOnlyList<SourceRecord> All()
    {
        lock (_lock)
            return [.. _sources.Values];
    }

    /// <summary>Bytes reserved by sources: the declared size, because partial uploads still grow.</summary>
    public long ReservedBytes()
    {
        lock (_lock)
            return _sources.Values.Sum(source => source.Size);
    }

    public SourceRecord? Find(string clientId, string fingerprint, long size)
    {
        lock (_lock)
        {
            return _sources.Values.FirstOrDefault(source =>
                string.Equals(source.ClientId, clientId, StringComparison.Ordinal)
                && string.Equals(source.Fingerprint, fingerprint, StringComparison.Ordinal)
                && source.Size == size);
        }
    }

    public SourceRecord Get(string clientId, string sourceId)
    {
        lock (_lock)
        {
            return _sources.TryGetValue(sourceId, out var record)
                && string.Equals(record.ClientId, clientId, StringComparison.Ordinal)
                ? record
                : throw AgentException.NotFound("The source");
        }
    }

    public SourceRecord Register(string clientId, CreateSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Fingerprint) || request.Size <= 0)
            throw AgentException.Invalid("A source needs a fingerprint and a positive size.");

        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (Find(clientId, request.Fingerprint, request.Size) is { } existing)
            {
                var touched = existing with { LastUsedAt = now };
                Save(touched);
                return touched;
            }

            var id = Guid.NewGuid().ToString("N");
            var record = new SourceRecord(id, clientId, request.Fingerprint, request.Size,
                Path.GetFileName(request.FileName), SafeExtension(request.FileName), now, now);
            Directory.CreateDirectory(paths.SourceDirectory(id));
            Save(record);
            logger.LogInformation("Registered source {SourceId} ({Size} bytes) for client {ClientId}", id, request.Size, clientId);
            return record;
        }
    }

    public SourceStatus Status(SourceRecord record) =>
        new(record.SourceId, record.Size, record.Complete ? record.Size : PartialLength(record), record.Complete);

    public string DataPath(SourceRecord record) =>
        Path.Combine(paths.SourceDirectory(record.SourceId), "source" + record.Extension);

    private string PartialPath(SourceRecord record) => DataPath(record) + ".partial";

    private long PartialLength(SourceRecord record)
    {
        var info = new FileInfo(PartialPath(record));
        return info.Exists ? info.Length : 0;
    }

    /// <summary>
    /// Appends request data at <paramref name="offset"/>. Every byte that arrives is kept, even when
    /// the connection drops mid-request, so the next attempt resumes where this one stopped.
    /// </summary>
    public async Task<SourceStatus> AppendAsync(string clientId, string sourceId, long offset, Stream body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var record = Get(clientId, sourceId);
        if (record.Complete)
            throw AgentException.Conflict(ErrorCodes.InvalidState, "The source is already complete.");

        var gate = _writeLocks.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var received = PartialLength(record);
            if (offset != received)
            {
                throw AgentException.Conflict(ErrorCodes.OffsetMismatch,
                    $"The upload must continue at byte {received}, not {offset}.");
            }

            await using (var file = new FileStream(PartialPath(record), FileMode.Append, FileAccess.Write,
                             FileShare.Read, CopyBufferSize, useAsync: true))
            {
                var buffer = new byte[CopyBufferSize];
                int read;
                // Stop at the declared size: the agent never stores more than it reserved.
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    if (file.Length + read > record.Size)
                        throw AgentException.Invalid("The upload is larger than the registered size.");
                    await file.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                }
            }

            Touch(record);
            return Status(Get(clientId, sourceId));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Verifies the SHA-256 and marks the source complete. On a mismatch the data is discarded so
    /// the client can upload it again.
    /// </summary>
    public async Task<SourceStatus> CompleteAsync(string clientId, string sourceId, string sha256, CancellationToken ct)
    {
        var record = Get(clientId, sourceId);
        if (record.Complete)
            return Status(record);

        var gate = _writeLocks.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var received = PartialLength(record);
            if (received != record.Size)
            {
                throw AgentException.Conflict(ErrorCodes.SourceIncomplete,
                    $"The agent has {received} of {record.Size} bytes.");
            }

            string actual;
            await using (var stream = new FileStream(PartialPath(record), FileMode.Open, FileAccess.Read,
                             FileShare.Read, CopyBufferSize, useAsync: true))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            }

            if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(PartialPath(record));
                logger.LogWarning("Discarded source {SourceId}: SHA-256 mismatch", sourceId);
                throw AgentException.Conflict(ErrorCodes.HashMismatch,
                    "The uploaded data does not match its SHA-256 checksum and was discarded.");
            }

            File.Move(PartialPath(record), DataPath(record), overwrite: true);
            var completed = record with { Complete = true, Sha256 = actual, LastUsedAt = timeProvider.GetUtcNow() };
            lock (_lock)
                Save(completed);
            logger.LogInformation("Source {SourceId} complete", sourceId);
            return Status(completed);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Touch(SourceRecord record)
    {
        lock (_lock)
        {
            if (_sources.TryGetValue(record.SourceId, out var current))
                Save(current with { LastUsedAt = timeProvider.GetUtcNow() });
        }
    }

    public void Delete(string sourceId)
    {
        lock (_lock)
        {
            _sources.Remove(sourceId);
            var directory = paths.SourceDirectory(sourceId);
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                logger.LogInformation("Deleted source {SourceId}", sourceId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Could not delete source {SourceId}", sourceId);
            }
        }
        _writeLocks.TryRemove(sourceId, out _);
    }

    private void Save(SourceRecord record)
    {
        _sources[record.SourceId] = record;
        AtomicFile.WriteJson(paths.SourceMeta(record.SourceId), record, AgentJsonContext.Default.SourceRecord);
    }

    internal static string SafeExtension(string fileName)
    {
        var extension = Path.GetExtension(Path.GetFileName(fileName ?? ""));
        if (extension.Length is < 2 || extension.Length > MaxExtensionLength + 1
            || !extension.AsSpan(1).ContainsOnlyAsciiLettersOrDigits())
        {
            return ".bin";
        }
        return extension.ToLowerInvariant();
    }
}

internal static class SpanExtensions
{
    public static bool ContainsOnlyAsciiLettersOrDigits(this ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c))
                return false;
        }
        return true;
    }
}
