using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

using FFApp.Core.Models;
using FFApp.Infrastructure;
using FFApp.Remote.Contracts;
using Microsoft.Extensions.Logging;

namespace FFApp.Remote;

/// <summary>
/// What the app must remember about a server job to continue it after a restart.
/// <see cref="Finished"/> marks a job that failed or was canceled and only waits for "Run again".
/// </summary>
public sealed record RemoteJobRecord(
    string Key,
    string DisplayLabel,
    JobDefinition Job,
    SourceMediaInfo MediaInfo,
    ResultMode ResultMode,
    string? ServerOutputRoot,
    RemotePhase Phase,
    DateTimeOffset CreatedAt,
    string? SourceId = null,
    string? ClientJobId = null,
    string? AgentJobId = null,
    string? PreviousAgentJobId = null,
    bool Finished = false);

public sealed record RemoteJobFile(ImmutableArray<RemoteJobRecord> Jobs, int SchemaVersion = 1);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RemoteJobFile))]
internal sealed partial class RemoteStoreJsonContext : JsonSerializerContext;

/// <summary>Server jobs of this app, persisted in <c>remote-jobs.json</c> so they survive a restart.</summary>
public sealed class RemoteJobStore(ILogger<RemoteJobStore> logger, string? path = null)
{
    public static readonly string FileName = "remote-jobs.json";

    private readonly string _path = path ?? Path.Combine(FFAppPaths.AppDataDirectory(), FileName);
    private readonly Lock _lock = new();
    private Dictionary<string, RemoteJobRecord>? _records;

    public IReadOnlyList<RemoteJobRecord> All()
    {
        lock (_lock)
            return [.. Records().Values.OrderBy(record => record.CreatedAt)];
    }

    public RemoteJobRecord? Find(string key)
    {
        lock (_lock)
            return Records().GetValueOrDefault(key);
    }

    public void Save(RemoteJobRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            Records()[record.Key] = record;
            Write();
        }
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            if (Records().Remove(key))
                Write();
        }
    }

    private Dictionary<string, RemoteJobRecord> Records()
    {
        if (_records is not null)
            return _records;
        _records = new Dictionary<string, RemoteJobRecord>(StringComparer.Ordinal);
        if (!File.Exists(_path))
            return _records;
        try
        {
            using var stream = File.OpenRead(_path);
            var file = JsonSerializer.Deserialize(stream, RemoteStoreJsonContext.Default.RemoteJobFile);
            foreach (var record in file?.Jobs ?? [])
                _records[record.Key] = record with { MediaInfo = WireNormalization.Normalize(record.MediaInfo) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file must not block the app; the jobs on the agent remain reachable there.
            logger.LogError(ex, "Could not read {Path}; server jobs from the previous session are not restored", _path);
        }
        return _records;
    }

    private void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            var temporary = _path + ".tmp";
            using (var stream = File.Create(temporary))
                JsonSerializer.Serialize(stream, new RemoteJobFile([.. Records().Values]), RemoteStoreJsonContext.Default.RemoteJobFile);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not save {Path}", _path);
        }
    }
}
