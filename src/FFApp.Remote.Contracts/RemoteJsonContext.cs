using System.Collections.Immutable;
using System.Text.Json.Serialization;

using FFApp.Core.Models;

namespace FFApp.Remote.Contracts;

/// <summary>
/// Source-generated serializer for the agent protocol. camelCase property names are a decision of
/// this contract only; enums carry their wire names per type.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(AgentInfo))]
[JsonSerializable(typeof(CreateSourceRequest))]
[JsonSerializable(typeof(SourceStatus))]
[JsonSerializable(typeof(CompleteSourceRequest))]
[JsonSerializable(typeof(CreateJobRequest))]
[JsonSerializable(typeof(AgentJob))]
[JsonSerializable(typeof(ImmutableArray<AgentJob>))]
[JsonSerializable(typeof(QueueState))]
[JsonSerializable(typeof(JobProgressEvent))]
[JsonSerializable(typeof(JobLogEvent))]
[JsonSerializable(typeof(UpdateRequest))]
[JsonSerializable(typeof(UpdateStatus))]
[JsonSerializable(typeof(JobDefinition))]
[JsonSerializable(typeof(SourceMediaInfo))]
public sealed partial class RemoteJsonContext : JsonSerializerContext;
