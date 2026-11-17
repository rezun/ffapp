using System.Collections.Immutable;

using FFApp.Agent.Storage;
using FFApp.Core;
using FFApp.Core.Models;

namespace FFApp.Agent.Jobs;

/// <summary>Throughput of recently completed jobs, persisted so estimates survive restarts.</summary>
public sealed class ThroughputHistory(AgentPaths paths, ILogger<ThroughputHistory> logger)
{
    private static readonly int MaxSamples = 50;

    private readonly Lock _lock = new();
    private ImmutableArray<ThroughputSample> _samples = [];

    public void Load()
    {
        try
        {
            var samples = AtomicFile.ReadJson(paths.Throughput, AgentJsonContext.Default.ImmutableArrayThroughputSample);
            lock (_lock)
                _samples = samples.IsDefault ? [] : samples;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Ignoring unreadable throughput history");
        }
    }

    public void Add(ThroughputSample sample)
    {
        lock (_lock)
        {
            _samples = [.. _samples.Add(sample).TakeLast(MaxSamples)];
            try
            {
                AtomicFile.WriteJson(paths.Throughput, _samples, AgentJsonContext.Default.ImmutableArrayThroughputSample);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not save throughput history");
            }
        }
    }

    public IReadOnlyList<QueueEstimateEntry> Entries()
    {
        lock (_lock)
        {
            return [.. _samples.Select(s => new QueueEstimateEntry(
                JobState.Completed, s.MediaSeconds, s.PassCount, ElapsedSeconds: s.ElapsedSeconds, Kind: s.Kind))];
        }
    }
}
