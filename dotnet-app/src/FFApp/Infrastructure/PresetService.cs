using System.Text.Json;
using System.Text.Json.Nodes;

using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure.Serialization;

namespace FFApp.Infrastructure;

public sealed class PresetStorageException(string message)
    : InvalidOperationException(message);

public sealed class PresetService(string? path = null) : IPresetService
{
    private readonly string _path = path ?? AppPaths.PresetsFilePath();

    public async Task<IReadOnlyList<PresetDefinition>> LoadAllAsync(
        CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return [];

        var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var schemaVersion = root.TryGetProperty("schema_version", out var versionProp)
            && versionProp.TryGetInt32(out var sv)
                ? sv
                : SettingsSerializer.PresetLibrarySchemaVersion;

        if (schemaVersion != SettingsSerializer.PresetLibrarySchemaVersion)
            return [];

        var presets = new List<PresetDefinition>();
        if (root.TryGetProperty("presets", out var presetsEl)
            && presetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in presetsEl.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                try
                {
                    presets.Add(SettingsSerializer.DeserializePreset(entry));
                }
                catch (PresetDecodeException)
                {
                    // Skip invalid preset entries.
                }
            }
        }

        presets.Sort((a, b) => string.Compare(
            a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return presets;
    }

    public async Task SaveAsync(PresetDefinition preset, CancellationToken ct = default)
    {
        var name = NormalizeName(preset.Name);
        var presets = new List<PresetDefinition>(await LoadAllAsync(ct).ConfigureAwait(false));

        var updated = new PresetDefinition(
            Name: name,
            Settings: preset.Settings,
            SchemaVersion: SettingsSerializer.PresetSchemaVersion);

        var replaced = false;
        for (var i = 0; i < presets.Count; i++)
        {
            if (string.Equals(presets[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                presets[i] = updated;
                replaced = true;
                break;
            }
        }

        if (!replaced)
            presets.Add(updated);

        await WritePresetsAsync(presets, ct).ConfigureAwait(false);
    }

    public async Task<PresetDefinition> RenameAsync(
        string currentName, string newName, CancellationToken ct = default)
    {
        var current = NormalizeName(currentName);
        var target = NormalizeName(newName);
        var presets = new List<PresetDefinition>(await LoadAllAsync(ct).ConfigureAwait(false));

        // Check for name collision (different preset with the target name)
        foreach (var preset in presets)
        {
            if (string.Equals(preset.Name, target, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(preset.Name, current, StringComparison.OrdinalIgnoreCase))
            {
                throw new PresetStorageException(
                    $"A preset named '{target}' already exists.");
            }
        }

        PresetDefinition? renamed = null;
        for (var i = 0; i < presets.Count; i++)
        {
            if (string.Equals(presets[i].Name, current, StringComparison.OrdinalIgnoreCase))
            {
                renamed = new PresetDefinition(
                    Name: target,
                    Settings: presets[i].Settings,
                    SchemaVersion: SettingsSerializer.PresetSchemaVersion);
                presets[i] = renamed;
                break;
            }
        }

        if (renamed is null)
            throw new PresetStorageException($"No preset named '{current}' exists.");

        await WritePresetsAsync(presets, ct).ConfigureAwait(false);
        return renamed;
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        var target = NormalizeName(name);
        var presets = new List<PresetDefinition>(await LoadAllAsync(ct).ConfigureAwait(false));

        var originalCount = presets.Count;
        presets.RemoveAll(p =>
            string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase));

        if (presets.Count == originalCount)
            throw new PresetStorageException($"No preset named '{target}' exists.");

        await WritePresetsAsync(presets, ct).ConfigureAwait(false);
    }

    // --- Private helpers ---

    private async Task WritePresetsAsync(
        List<PresetDefinition> presets, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        presets.Sort((a, b) => string.Compare(
            a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var presetsArray = new JsonArray();
        foreach (var preset in presets)
            presetsArray.Add(SettingsSerializer.SerializePreset(preset));

        var payload = new JsonObject
        {
            ["schema_version"] = SettingsSerializer.PresetLibrarySchemaVersion,
            ["presets"] = presetsArray,
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = payload.ToJsonString(options);
        await File.WriteAllTextAsync(_path, json, ct).ConfigureAwait(false);
    }

    private static string NormalizeName(string value)
    {
        var name = value.Trim();
        if (string.IsNullOrEmpty(name))
            throw new PresetStorageException("Preset name cannot be empty.");
        return name;
    }
}
