using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFApp.Core.Serialization;

/// <summary>
/// Reads a JSON object into an immutable dictionary with case-insensitive keys, matching how
/// FFprobe tags are looked up. The default converter would drop the comparer.
/// </summary>
public sealed class CaseInsensitiveStringDictionaryConverter : JsonConverter<ImmutableDictionary<string, string>>
{
    public override ImmutableDictionary<string, string> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a JSON object of string values.");

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return builder.ToImmutable();
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected a property name.");
            var key = reader.GetString() ?? throw new JsonException("Property names cannot be null.");
            reader.Read();
            builder[key] = reader.GetString() ?? throw new JsonException($"Tag {key} has no string value.");
        }
        throw new JsonException("Unexpected end of JSON while reading tags.");
    }

    public override void Write(
        Utf8JsonWriter writer, ImmutableDictionary<string, string> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        foreach (var (key, tagValue) in value)
            writer.WriteString(key, tagValue);
        writer.WriteEndObject();
    }
}
