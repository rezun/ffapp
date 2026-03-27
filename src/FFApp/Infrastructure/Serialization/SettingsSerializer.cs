using System.Text.Json;
using System.Text.Json.Nodes;

using FFApp.Core.Models;
using FFApp.Core.Validation;

namespace FFApp.Infrastructure.Serialization;

public sealed class SettingsDecodeException(string message)
    : InvalidOperationException(message);

public sealed class PresetDecodeException(string message)
    : InvalidOperationException(message);

public static class SettingsSerializer
{
    public const int SettingsSchemaVersion = 4;
    public const int PresetSchemaVersion = 1;
    public const int PresetLibrarySchemaVersion = 1;

    // --- OutputSettings serialization ---

    public static JsonObject SerializeOutputSettings(OutputSettings settings)
    {
        return new JsonObject
        {
            ["container"] = EnumToString(settings.Container),
            ["video_codec"] = EnumToString(settings.VideoCodec),
            ["encoding_strategy"] = EnumToString(settings.EncodingStrategy),
            ["resolution_preset"] = EnumToString(settings.ResolutionPreset),
            ["video_encoder_options"] = SerializeVideoEncoderOptions(settings.VideoEncoderOptions),
            ["audio_options"] = new JsonObject
            {
                ["mode"] = EnumToString(settings.AudioOptions.Mode),
                ["codec"] = settings.AudioOptions.Codec is { } ac ? EnumToString(ac) : null,
                ["bitrate"] = settings.AudioOptions.Bitrate,
                ["remix_to_stereo"] = settings.AudioOptions.RemixToStereo,
            },
            ["stream_handling"] = new JsonObject
            {
                ["remove_subtitles"] = settings.StreamHandling.RemoveSubtitles,
                ["remove_data"] = settings.StreamHandling.RemoveData,
            },
            ["target_file_size"] = new JsonObject
            {
                ["target_size_mb"] = settings.TargetFileSize.TargetSizeMb,
                ["two_pass"] = settings.TargetFileSize.TwoPass,
                ["muxing_overhead_ratio"] = settings.TargetFileSize.MuxingOverheadRatio,
            },
            ["target_bitrate"] = new JsonObject
            {
                ["video_bitrate"] = settings.TargetBitrate.VideoBitrate,
                ["two_pass"] = settings.TargetBitrate.TwoPass,
            },
            ["target_quality"] = SerializeQualityOptions(settings.TargetQuality),
        };
    }

    public static OutputSettings DeserializeOutputSettings(JsonElement element)
    {
        var container = ReadEnum<OutputContainer>(element, "container", OutputContainer.Mp4);
        var videoCodec = ReadEnum<VideoCodec>(element, "video_codec", VideoCodec.H264);
        var encodingStrategy = ReadEnum<EncodingStrategy>(
            element, "encoding_strategy", EncodingStrategy.TargetQuality);
        var resolutionPreset = ReadEnum<ResolutionPreset>(
            element, "resolution_preset", ResolutionPreset.Original);

        ValidateContainerCodecs(container, videoCodec);

        // Audio
        var audioEl = GetObjectOrEmpty(element, "audio_options");
        var audioMode = ReadEnum<AudioMode>(audioEl, "mode", AudioMode.Copy);
        var audioCodec = ReadOptionalEnum<AudioCodec>(audioEl, "codec");
        if (audioCodec is not null)
            ValidateAudioCodec(container, audioCodec.Value);

        var audioBitrate = ReadOptionalInt(audioEl, "bitrate");
        var remixToStereo = ReadBool(audioEl, "remix_to_stereo");

        if (audioMode != AudioMode.Reencode)
        {
            audioCodec = null;
            audioBitrate = null;
            remixToStereo = false;
        }

        // Stream handling
        var streamEl = GetObjectOrEmpty(element, "stream_handling");
        var streamHandling = new StreamHandlingOptions(
            RemoveSubtitles: ReadBool(streamEl, "remove_subtitles"),
            RemoveData: ReadBool(streamEl, "remove_data"));

        // Target file size
        var fileSizeEl = GetObjectOrEmpty(element, "target_file_size");
        var targetFileSize = new TargetFileSizeOptions(
            TargetSizeMb: ReadInt(fileSizeEl, "target_size_mb", 50),
            TwoPass: ReadBool(fileSizeEl, "two_pass"),
            MuxingOverheadRatio: ReadDouble(fileSizeEl, "muxing_overhead_ratio", 0.015));

        // Target bitrate
        var bitrateEl = GetObjectOrEmpty(element, "target_bitrate");
        var targetBitrate = new TargetBitrateOptions(
            VideoBitrate: ReadInt(bitrateEl, "video_bitrate", 3_000_000),
            TwoPass: ReadBool(bitrateEl, "two_pass"));

        // Video encoder options
        VideoEncoderOptions? videoEncoderOptions = null;
        if (element.TryGetProperty("video_encoder_options", out var encoderEl)
            && encoderEl.ValueKind == JsonValueKind.Object)
        {
            videoEncoderOptions = DeserializeVideoEncoderOptions(videoCodec, encoderEl);
        }

        // Target quality
        var qualityEl = GetObjectOrEmpty(element, "target_quality");
        var targetQuality = DeserializeQualityOptions(videoCodec, qualityEl);

        // VPx fallback: if no explicit encoder options, derive from quality speed
        if (videoEncoderOptions is null && videoCodec is VideoCodec.Vp8 or VideoCodec.Vp9)
        {
            var cpuUsed = ReadInt(qualityEl, "speed",
                EncoderDefaults.DefaultVpxCpuUsed(videoCodec));
            if (cpuUsed < CompatibilityData.VpxSpeedMin || cpuUsed > CompatibilityData.VpxSpeedMax)
                throw new SettingsDecodeException("Invalid VP8/VP9 CPU used setting.");
            videoEncoderOptions = new VpxEncoderOptions(videoCodec, CpuUsed: cpuUsed);
        }

        return new OutputSettings(
            Container: container,
            VideoCodec: videoCodec,
            EncodingStrategy: encodingStrategy,
            ResolutionPreset: resolutionPreset,
            VideoEncoderOptions: videoEncoderOptions,
            AudioOptions: new AudioOptions(
                Mode: audioMode,
                Codec: audioCodec,
                Bitrate: audioBitrate,
                RemixToStereo: remixToStereo),
            StreamHandling: streamHandling,
            TargetFileSize: targetFileSize,
            TargetBitrate: targetBitrate,
            TargetQuality: targetQuality);
    }

    // --- Preset serialization ---

    public static JsonObject SerializePreset(PresetDefinition preset)
    {
        return new JsonObject
        {
            ["name"] = preset.Name,
            ["schema_version"] = preset.SchemaVersion,
            ["settings"] = SerializeOutputSettings(preset.Settings),
        };
    }

    public static PresetDefinition DeserializePreset(JsonElement element)
    {
        var name = element.TryGetProperty("name", out var nameProp)
            ? nameProp.GetString()?.Trim() ?? ""
            : "";
        if (string.IsNullOrEmpty(name))
            throw new PresetDecodeException("Preset name is required.");

        var schemaVersion = ReadInt(element, "schema_version", PresetSchemaVersion);
        if (schemaVersion != PresetSchemaVersion)
            throw new PresetDecodeException(
                $"Unsupported preset schema version: {schemaVersion}.");

        OutputSettings settings;
        try
        {
            var settingsEl = GetObjectOrEmpty(element, "settings");
            settings = DeserializeOutputSettings(settingsEl);
        }
        catch (SettingsDecodeException ex)
        {
            throw new PresetDecodeException(ex.Message);
        }

        return new PresetDefinition(
            Name: name,
            Settings: settings,
            SchemaVersion: schemaVersion);
    }

    // --- Video encoder options ---

    private static JsonNode? SerializeVideoEncoderOptions(VideoEncoderOptions? options)
    {
        if (options is null)
            return null;

        return options switch
        {
            VpxEncoderOptions vpx => new JsonObject
            {
                ["codec"] = EnumToString(vpx.Codec),
                ["cpu_used"] = vpx.CpuUsed,
                ["tile_columns"] = vpx.TileColumns,
                ["tile_rows"] = vpx.TileRows,
            },
            H264EncoderOptions h264 => new JsonObject
            {
                ["codec"] = EnumToString(h264.Codec),
                ["preset"] = h264.Preset,
                ["profile"] = h264.Profile,
            },
            H265EncoderOptions h265 => new JsonObject
            {
                ["codec"] = EnumToString(h265.Codec),
                ["preset"] = h265.Preset,
                ["profile"] = h265.Profile,
            },
            _ => throw new InvalidOperationException(
                $"Unknown VideoEncoderOptions type: {options.GetType().Name}"),
        };
    }

    private static JsonObject SerializeQualityOptions(QualityOptions options)
    {
        return options switch
        {
            H264QualityOptions h264 => new JsonObject
            {
                ["codec"] = EnumToString(h264.Codec),
                ["crf"] = h264.Crf,
                ["preset"] = h264.Preset,
                ["profile"] = h264.Profile,
            },
            H265QualityOptions h265 => new JsonObject
            {
                ["codec"] = EnumToString(h265.Codec),
                ["crf"] = h265.Crf,
                ["preset"] = h265.Preset,
                ["profile"] = h265.Profile,
            },
            Vp8QualityOptions vp8 => new JsonObject
            {
                ["codec"] = EnumToString(vp8.Codec),
                ["quality"] = vp8.Quality,
                ["two_pass"] = vp8.TwoPass,
            },
            Vp9QualityOptions vp9 => new JsonObject
            {
                ["codec"] = EnumToString(vp9.Codec),
                ["quality"] = vp9.Quality,
                ["two_pass"] = vp9.TwoPass,
            },
            _ => throw new InvalidOperationException(
                $"Unknown QualityOptions type: {options.GetType().Name}"),
        };
    }

    private static VideoEncoderOptions DeserializeVideoEncoderOptions(
        VideoCodec videoCodec, JsonElement element)
    {
        if (videoCodec == VideoCodec.H264)
        {
            var preset = ReadString(element, "preset", "medium");
            var profile = ReadString(element, "profile", "high");
            if (!CompatibilityData.QualityPresetOptions(videoCodec).Contains(preset))
                throw new SettingsDecodeException("Invalid H.264 encoder preset.");
            if (!CompatibilityData.QualityProfileOptions(videoCodec).Contains(profile))
                throw new SettingsDecodeException("Invalid H.264 encoder profile.");
            return new H264EncoderOptions(Preset: preset, Profile: profile);
        }

        if (videoCodec == VideoCodec.H265)
        {
            var preset = ReadString(element, "preset", "medium");
            var profile = ReadString(element, "profile", "main");
            if (!CompatibilityData.QualityPresetOptions(videoCodec).Contains(preset))
                throw new SettingsDecodeException("Invalid H.265 encoder preset.");
            if (!CompatibilityData.QualityProfileOptions(videoCodec).Contains(profile))
                throw new SettingsDecodeException("Invalid H.265 encoder profile.");
            return new H265EncoderOptions(Preset: preset, Profile: profile);
        }

        // VP8 or VP9
        var cpuUsed = ReadInt(element, "cpu_used",
            EncoderDefaults.DefaultVpxCpuUsed(videoCodec));
        if (cpuUsed < CompatibilityData.VpxSpeedMin || cpuUsed > CompatibilityData.VpxSpeedMax)
            throw new SettingsDecodeException("Invalid VP8/VP9 CPU used setting.");
        var tileColumns = ReadInt(element, "tile_columns", 0);
        if (tileColumns < CompatibilityData.VpxTileColumnsMin || tileColumns > CompatibilityData.VpxTileColumnsMax)
            throw new SettingsDecodeException("Invalid VP8/VP9 tile columns setting.");
        var tileRows = ReadInt(element, "tile_rows", 0);
        if (tileRows < CompatibilityData.VpxTileRowsMin || tileRows > CompatibilityData.VpxTileRowsMax)
            throw new SettingsDecodeException("Invalid VP8/VP9 tile rows setting.");
        return new VpxEncoderOptions(videoCodec, CpuUsed: cpuUsed, TileColumns: tileColumns, TileRows: tileRows);
    }

    private static QualityOptions DeserializeQualityOptions(
        VideoCodec videoCodec, JsonElement element)
    {
        if (videoCodec == VideoCodec.H264)
        {
            var preset = ReadString(element, "preset", "medium");
            var profile = ReadString(element, "profile", "high");
            if (!CompatibilityData.QualityPresetOptions(videoCodec).Contains(preset))
                throw new SettingsDecodeException("Invalid H.264 quality preset.");
            if (!CompatibilityData.QualityProfileOptions(videoCodec).Contains(profile))
                throw new SettingsDecodeException("Invalid H.264 quality profile.");
            return new H264QualityOptions(
                Crf: ReadInt(element, "crf", 23),
                Preset: preset,
                Profile: profile);
        }

        if (videoCodec == VideoCodec.H265)
        {
            var preset = ReadString(element, "preset", "medium");
            var profile = ReadString(element, "profile", "main");
            if (!CompatibilityData.QualityPresetOptions(videoCodec).Contains(preset))
                throw new SettingsDecodeException("Invalid H.265 quality preset.");
            if (!CompatibilityData.QualityProfileOptions(videoCodec).Contains(profile))
                throw new SettingsDecodeException("Invalid H.265 quality profile.");
            return new H265QualityOptions(
                Crf: ReadInt(element, "crf", 28),
                Preset: preset,
                Profile: profile);
        }

        if (videoCodec == VideoCodec.Vp8)
        {
            return new Vp8QualityOptions(
                Quality: ReadInt(element, "quality", 31),
                TwoPass: ReadBool(element, "two_pass"));
        }

        return new Vp9QualityOptions(
            Quality: ReadInt(element, "quality", 32),
            TwoPass: ReadBool(element, "two_pass"));
    }

    // --- Validation helpers ---

    private static void ValidateContainerCodecs(
        OutputContainer container, VideoCodec videoCodec)
    {
        if (!CompatibilityData.VideoCodecMatrix[container].Contains(videoCodec))
        {
            throw new SettingsDecodeException(
                $"{EnumToString(videoCodec)} is not supported in the " +
                $"{EnumToString(container)} container.");
        }
    }

    private static void ValidateAudioCodec(
        OutputContainer container, AudioCodec audioCodec)
    {
        if (!CompatibilityData.AudioCodecMatrix[container].Contains(audioCodec))
        {
            throw new SettingsDecodeException(
                $"{EnumToString(audioCodec)} is not supported in the " +
                $"{EnumToString(container)} container.");
        }
    }

    // --- JSON reading helpers ---

    private static JsonElement GetObjectOrEmpty(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Object)
        {
            return prop;
        }

        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static string ReadString(
        JsonElement element, string property, string defaultValue)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return defaultValue;
        if (!element.TryGetProperty(property, out var prop))
            return defaultValue;
        return prop.GetString() ?? defaultValue;
    }

    private static int ReadInt(
        JsonElement element, string property, int defaultValue)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return defaultValue;
        if (!element.TryGetProperty(property, out var prop))
            return defaultValue;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var intValue))
            return intValue;
        if (prop.ValueKind == JsonValueKind.String
            && int.TryParse(prop.GetString(), out var parsed))
            return parsed;
        return defaultValue;
    }

    private static int? ReadOptionalInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        if (!element.TryGetProperty(property, out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.Null)
            return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var intValue))
            return intValue;
        return null;
    }

    private static double ReadDouble(
        JsonElement element, string property, double defaultValue)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return defaultValue;
        if (!element.TryGetProperty(property, out var prop))
            return defaultValue;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var dblValue))
            return dblValue;
        return defaultValue;
    }

    private static bool ReadBool(
        JsonElement element, string property, bool defaultValue = false)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return defaultValue;
        if (!element.TryGetProperty(property, out var prop))
            return defaultValue;
        if (prop.ValueKind is JsonValueKind.True)
            return true;
        if (prop.ValueKind is JsonValueKind.False)
            return false;
        return defaultValue;
    }

    private static T ReadEnum<T>(
        JsonElement element, string property, T defaultValue)
        where T : struct, Enum
    {
        var str = ReadString(element, property, "");
        if (string.IsNullOrEmpty(str))
            return defaultValue;

        return ParseEnum<T>(str)
            ?? throw new SettingsDecodeException(
                $"Invalid value '{str}' for {typeof(T).Name}.");
    }

    private static T? ReadOptionalEnum<T>(JsonElement element, string property)
        where T : struct, Enum
    {
        var str = ReadString(element, property, "");
        if (string.IsNullOrEmpty(str))
            return null;

        return ParseEnum<T>(str)
            ?? throw new SettingsDecodeException(
                $"Invalid value '{str}' for {typeof(T).Name}.");
    }

    private static T? ParseEnum<T>(string value) where T : struct, Enum
    {
        // Use JsonSerializer to parse enum values via the JsonStringEnumMemberName attributes.
        var json = $"\"{value}\"";
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string EnumToString<T>(T value) where T : struct, Enum
    {
        // Serialize the enum to its JsonStringEnumMemberName value, stripping quotes.
        var json = JsonSerializer.Serialize(value);
        return json.Trim('"');
    }
}
