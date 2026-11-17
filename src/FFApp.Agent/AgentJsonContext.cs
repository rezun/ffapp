using System.Collections.Immutable;
using System.Text.Json.Serialization;

using FFApp.Agent.Clients;
using FFApp.Agent.Jobs;
using FFApp.Agent.Storage;
using FFApp.Agent.Updates;

namespace FFApp.Agent;

/// <summary>Serializer for the agent's own state files. Not part of the protocol.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClientList))]
[JsonSerializable(typeof(SourceRecord))]
[JsonSerializable(typeof(AgentJobRecord))]
[JsonSerializable(typeof(ImmutableArray<ThroughputSample>))]
[JsonSerializable(typeof(UpdateRecord))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
