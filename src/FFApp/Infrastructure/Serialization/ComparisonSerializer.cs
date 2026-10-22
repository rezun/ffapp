using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using FFApp.Core.Models;

namespace FFApp.Infrastructure.Serialization;

public static class ComparisonSerializer
{
    public const int SchemaVersion = 1;

    public static JsonObject Serialize(ComparisonSession session, JsonSerializerOptions options) => new()
    {
        ["schema_version"] = SchemaVersion,
        ["source_path"] = session.SourcePath,
        ["source_fingerprint"] = session.SourceFingerprint,
        ["baseline"] = SettingsSerializer.SerializeOutputSettings(session.Baseline),
        ["range_start"] = session.RangeStart,
        ["range_end"] = session.RangeEnd,
        ["crop"] = JsonSerializer.SerializeToNode(session.Crop, options),
        ["audio_selection"] = JsonSerializer.SerializeToNode(session.SelectedAudioStreamIndices, options),
        ["samples"] = JsonSerializer.SerializeToNode(session.Samples, options),
        ["variants"] = new JsonArray(session.Variants.Select(v => (JsonNode)new JsonObject
        {
            ["id"] = v.Id, ["name"] = v.Name,
            ["settings"] = SettingsSerializer.SerializeOutputSettings(v.Settings),
        }).ToArray()),
        ["results"] = JsonSerializer.SerializeToNode(session.Results, options),
    };

    public static ComparisonSession Deserialize(JsonElement root, JsonSerializerOptions options)
    {
        if (root.GetProperty("schema_version").GetInt32() != SchemaVersion)
            throw new SettingsDecodeException("This comparison was saved by an unsupported version of FFApp.");
        return new ComparisonSession(
            RequiredString(root, "source_path"), RequiredString(root, "source_fingerprint"),
            SettingsSerializer.DeserializeOutputSettings(root.GetProperty("baseline")),
            root.GetProperty("range_start").GetDouble(), root.GetProperty("range_end").GetDouble(),
            root.GetProperty("crop").Deserialize<CropRect>(options),
            root.GetProperty("samples").Deserialize<ImmutableArray<ComparisonSample>>(options),
            [.. root.GetProperty("variants").EnumerateArray().Select(v => new ComparisonVariant(
                RequiredString(v, "id"), RequiredString(v, "name"),
                SettingsSerializer.DeserializeOutputSettings(v.GetProperty("settings"))))],
            root.GetProperty("results").Deserialize<ImmutableArray<ComparisonResult>>(options),
            root.TryGetProperty("audio_selection", out var selection)
                ? selection.Deserialize<ImmutableArray<int>?>(options) : null);
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.GetProperty(name).GetString() ?? throw new SettingsDecodeException($"Missing comparison field: {name}.");
}
