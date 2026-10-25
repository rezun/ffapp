using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure.Serialization;

namespace FFApp.Infrastructure;

public sealed class SettingsService(string? path = null) : ISettingsService
{
    private readonly string _path = path ?? AppPaths.SettingsFilePath();

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return new AppSettings();

        var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var schemaVersion = root.TryGetProperty("schema_version", out var versionProp)
            && versionProp.TryGetInt32(out var sv)
                ? sv
                : SettingsSerializer.SettingsSchemaVersion;

        var executablePaths = DeserializeExecutablePaths(root);

        OutputSettings lastUsedSettings;
        try
        {
            var settingsEl = SettingsSerializer.GetObjectOrEmpty(root, "last_used_settings");
            lastUsedSettings = SettingsSerializer.DeserializeOutputSettings(settingsEl);
        }
        catch (SettingsDecodeException)
        {
            lastUsedSettings = new OutputSettings();
        }

        var lastUsedPresetName = ReadOptionalString(root, "last_used_preset_name");

        var splitterLayout = DeserializeSplitterLayout(root);
        var windowGeometry = DeserializeWindowGeometry(root);
        var themeStr = ReadOptionalString(root, "theme_preference") ?? "system";
        var themePreference = Enum.TryParse<ThemePreference>(themeStr, ignoreCase: true, out var tp)
            ? tp
            : ThemePreference.System;

        return new AppSettings
        {
            SchemaVersion = schemaVersion,
            ExecutablePaths = executablePaths,
            LastUsedSettings = lastUsedSettings,
            LastUsedPresetName = lastUsedPresetName,
            SplitterLayout = splitterLayout,
            WindowGeometry = windowGeometry,
            ThemePreference = themePreference,
            UseLowPriorityTranscoding = SettingsSerializer.ReadBool(root, "low_priority_transcoding"),
            ComparisonCacheLimitGiB = root.TryGetProperty("comparison_cache_limit_gib", out var cacheLimit)
                && cacheLimit.ValueKind == JsonValueKind.Number && cacheLimit.TryGetInt32(out var gib)
                && gib is >= 1 and <= 1024 ? gib : 5,
        };
    }

    public static WindowGeometry LoadWindowGeometrySync(string? path = null)
    {
        var filePath = path ?? AppPaths.SettingsFilePath();
        if (!File.Exists(filePath))
            return new WindowGeometry();

        try
        {
            using var stream = File.OpenRead(filePath);
            using var doc = JsonDocument.Parse(stream);
            return DeserializeWindowGeometry(doc.RootElement);
        }
        catch
        {
            return new WindowGeometry();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var payload = new JsonObject
        {
            ["schema_version"] = SettingsSerializer.SettingsSchemaVersion,
            ["executable_paths"] = new JsonObject
            {
                ["ffmpeg"] = settings.ExecutablePaths.Ffmpeg,
                ["ffprobe"] = settings.ExecutablePaths.Ffprobe,
                ["mkvpropedit"] = settings.ExecutablePaths.Mkvpropedit,
            },
            ["last_used_settings"] = SettingsSerializer.SerializeOutputSettings(
                settings.LastUsedSettings),
            ["last_used_preset_name"] = settings.LastUsedPresetName,
            ["splitter_layout"] = new JsonObject
            {
                ["main_splitter_sizes"] = ToJsonArray(settings.SplitterLayout.MainSplitterSizes),
                ["queue_splitter_sizes"] = ToJsonArray(settings.SplitterLayout.QueueSplitterSizes),
                ["preview_height"] = settings.SplitterLayout.PreviewHeight,
            },
            ["window_geometry"] = new JsonObject
            {
                ["x"] = settings.WindowGeometry.X,
                ["y"] = settings.WindowGeometry.Y,
                ["width"] = settings.WindowGeometry.Width,
                ["height"] = settings.WindowGeometry.Height,
                ["is_maximized"] = settings.WindowGeometry.IsMaximized,
            },
            ["low_priority_transcoding"] = settings.UseLowPriorityTranscoding,
            ["comparison_cache_limit_gib"] = settings.ComparisonCacheLimitGiB,
            ["theme_preference"] = settings.ThemePreference.ToString().ToLowerInvariant(),
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = payload.ToJsonString(options);
        await File.WriteAllTextAsync(_path, json, ct).ConfigureAwait(false);
    }

    // --- Private helpers ---

    private static ExecutablePaths DeserializeExecutablePaths(JsonElement root)
    {
        if (!root.TryGetProperty("executable_paths", out var pathsEl)
            || pathsEl.ValueKind != JsonValueKind.Object)
        {
            return ExecutablePaths.Empty;
        }

        var ffmpeg = ReadOptionalString(pathsEl, "ffmpeg");
        var ffprobe = ReadOptionalString(pathsEl, "ffprobe");
        var mkvpropedit = ReadOptionalString(pathsEl, "mkvpropedit");
        return new ExecutablePaths(
            Ffmpeg: ffmpeg,
            Ffprobe: ffprobe,
            Mkvpropedit: mkvpropedit);
    }

    private static SplitterLayout DeserializeSplitterLayout(JsonElement root)
    {
        var el = SettingsSerializer.GetObjectOrEmpty(root, "splitter_layout");
        return new SplitterLayout
        {
            MainSplitterSizes = ReadIntArray(el, "main_splitter_sizes"),
            QueueSplitterSizes = ReadIntArray(el, "queue_splitter_sizes"),
            PreviewHeight = SettingsSerializer.ReadOptionalInt(el, "preview_height") ?? 0,
        };
    }

    private static WindowGeometry DeserializeWindowGeometry(JsonElement root)
    {
        var el = SettingsSerializer.GetObjectOrEmpty(root, "window_geometry");
        return new WindowGeometry(
            X: SettingsSerializer.ReadOptionalInt(el, "x"),
            Y: SettingsSerializer.ReadOptionalInt(el, "y"),
            Width: ReadOptionalPositiveInt(el, "width"),
            Height: ReadOptionalPositiveInt(el, "height"),
            IsMaximized: SettingsSerializer.ReadBool(el, "is_maximized"));
    }

    private static string? ReadOptionalString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop))
            return null;
        var str = prop.GetString();
        return string.IsNullOrEmpty(str) ? null : str;
    }

    private static int? ReadOptionalPositiveInt(JsonElement element, string property)
    {
        var value = SettingsSerializer.ReadOptionalInt(element, property);
        return value is > 0 ? value : null;
    }

    private static ImmutableArray<int> ReadIntArray(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return [];
        if (!element.TryGetProperty(property, out var prop))
            return [];
        if (prop.ValueKind != JsonValueKind.Array)
            return [];

        var builder = ImmutableArray.CreateBuilder<int>(prop.GetArrayLength());
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var value))
                return [];
            if (value < 0)
                return [];
            builder.Add(value);
        }
        return builder.ToImmutable();
    }

    private static JsonArray ToJsonArray(ImmutableArray<int> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }
}
