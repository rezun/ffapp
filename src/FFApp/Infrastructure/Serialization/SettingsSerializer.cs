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
    public const int SettingsSchemaVersion = 5;
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
            ["frame_rate_preset"] = EnumToString(settings.FrameRatePreset),
            ["video_encoder_options"] = SerializeVideoEncoderOptions(settings.VideoEncoderOptions),
            ["audio_options"] = new JsonObject
            {
                ["mode"] = EnumToString(settings.AudioOptions.Mode),
                ["codec"] = settings.AudioOptions.Codec is { } ac ? EnumToString(ac) : null,
                ["bitrate"] = settings.AudioOptions.Bitrate,
                ["audio_channels"] = settings.AudioOptions.AudioChannels,
                ["sample_rate"] = settings.AudioOptions.SampleRate,
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
                ["absolute_overhead_bytes"] = settings.TargetFileSize.AbsoluteOverheadBytes,
            },
            ["target_bitrate"] = new JsonObject
            {
                ["video_bitrate"] = settings.TargetBitrate.VideoBitrate,
                ["two_pass"] = settings.TargetBitrate.TwoPass,
            },
            ["target_quality"] = SerializeQualityOptions(settings.TargetQuality),
            ["hdr_mode"] = EnumToString(settings.HdrMode),
            ["tone_map_settings"] = new JsonObject
            {
                ["algorithm"] = EnumToString(settings.ToneMapSettings.Algorithm),
                ["desaturation"] = settings.ToneMapSettings.Desaturation,
                ["peak_nits"] = settings.ToneMapSettings.PeakNits,
            },
            ["keyframe_interval"] = settings.KeyframeInterval,
            ["keyframe_unit"] = EnumToString(settings.KeyframeUnit),
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
        var frameRatePreset = ReadEnum<FrameRatePreset>(
            element, "frame_rate_preset", FrameRatePreset.Original);

        ValidateContainerCodecs(container, videoCodec);

        // Audio
        var audioEl = GetObjectOrEmpty(element, "audio_options");
        var audioMode = ReadEnum<AudioMode>(audioEl, "mode", AudioMode.Copy);
        var audioCodec = ReadOptionalEnum<AudioCodec>(audioEl, "codec");
        if (audioCodec is not null)
            ValidateAudioCodec(container, audioCodec.Value);

        var audioBitrate = ReadOptionalInt(audioEl, "bitrate");
        var audioChannels = ReadOptionalInt(audioEl, "audio_channels")
            ?? (ReadBool(audioEl, "remix_to_stereo") ? 2 : null);
        var sampleRate = ReadOptionalInt(audioEl, "sample_rate");

        if (audioMode != AudioMode.Reencode)
        {
            audioCodec = null;
            audioBitrate = null;
            audioChannels = null;
            sampleRate = null;
        }

        // Stream handling
        var streamEl = GetObjectOrEmpty(element, "stream_handling");
        var streamHandling = new StreamHandlingOptions(
            RemoveSubtitles: ReadBool(streamEl, "remove_subtitles"),
            RemoveData: ReadBool(streamEl, "remove_data"));

        // Target file size
        var fileSizeEl = GetObjectOrEmpty(element, "target_file_size");
        var targetFileSize = new TargetFileSizeOptions(
            TargetSizeMb: ReadInt(fileSizeEl, "target_size_mb", FileSizeDefaults.TargetSizeMb),
            TwoPass: ReadBool(fileSizeEl, "two_pass"),
            MuxingOverheadRatio: ReadDouble(fileSizeEl, "muxing_overhead_ratio", FileSizeDefaults.MuxingOverheadRatio),
            AbsoluteOverheadBytes: ReadInt(fileSizeEl, "absolute_overhead_bytes", FileSizeDefaults.AbsoluteOverheadBytes));

        // Target bitrate
        var bitrateEl = GetObjectOrEmpty(element, "target_bitrate");
        var targetBitrate = new TargetBitrateOptions(
            VideoBitrate: ReadInt(bitrateEl, "video_bitrate", 3_000_000),
            TwoPass: ReadBool(bitrateEl, "two_pass"));

        // Target quality
        var qualityEl = GetObjectOrEmpty(element, "target_quality");
        var targetQuality = DeserializeQualityOptions(videoCodec, qualityEl);

        // Video encoder options
        VideoEncoderOptions? videoEncoderOptions = null;
        if (element.TryGetProperty("video_encoder_options", out var encoderEl)
            && encoderEl.ValueKind == JsonValueKind.Object)
        {
            videoEncoderOptions = DeserializeVideoEncoderOptions(videoCodec, encoderEl);
        }

        videoEncoderOptions ??= DefaultVideoEncoderOptions(videoCodec, targetQuality, qualityEl);

        // HDR settings
        var hdrMode = ReadEnum<HdrMode>(element, "hdr_mode", HdrMode.Preserve);
        var toneMapEl = GetObjectOrEmpty(element, "tone_map_settings");
        var toneMapSettings = new ToneMapSettings(
            Algorithm: ReadEnum<ToneMapAlgorithm>(toneMapEl, "algorithm", ToneMapAlgorithm.Hable),
            Desaturation: ReadDouble(toneMapEl, "desaturation", 2.0),
            PeakNits: ReadOptionalInt(toneMapEl, "peak_nits"));

        // Keyframe interval
        var keyframeInterval = ReadOptionalInt(element, "keyframe_interval");
        var keyframeUnit = ReadEnum<KeyframeUnit>(element, "keyframe_unit", KeyframeUnit.Frames);

        return new OutputSettings(
            Container: container,
            VideoCodec: videoCodec,
            EncodingStrategy: encodingStrategy,
            ResolutionPreset: resolutionPreset,
            FrameRatePreset: frameRatePreset,
            VideoEncoderOptions: videoEncoderOptions,
            AudioOptions: new AudioOptions(
                Mode: audioMode,
                Codec: audioCodec,
                Bitrate: audioBitrate,
                AudioChannels: audioChannels,
                SampleRate: sampleRate),
            StreamHandling: streamHandling,
            TargetFileSize: targetFileSize,
            TargetBitrate: targetBitrate,
            TargetQuality: targetQuality,
            HdrMode: hdrMode,
            ToneMapSettings: toneMapSettings,
            KeyframeInterval: keyframeInterval,
            KeyframeUnit: keyframeUnit);
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
                ["additional_options"] = vpx.AdditionalOptions,
            },
            H26xEncoderOptions h26x => new JsonObject
            {
                ["codec"] = EnumToString(h26x.Codec),
                ["preset"] = h26x.Preset,
                ["profile"] = h26x.Profile,
                ["additional_options"] = h26x.AdditionalOptions,
            },
            Av1EncoderOptions av1 => new JsonObject
            {
                ["codec"] = EnumToString(av1.Codec),
                ["preset"] = av1.Preset,
                ["tune"] = av1.Tune,
                ["enable_overlays"] = av1.EnableOverlays,
                ["scm"] = av1.Scm,
                ["enable_variance_boost"] = av1.EnableVarianceBoost,
                ["additional_options"] = av1.AdditionalOptions,
            },
            _ => throw new InvalidOperationException(
                $"Unknown VideoEncoderOptions type: {options.GetType().Name}"),
        };
    }

    private static JsonObject SerializeQualityOptions(QualityOptions options)
    {
        return options switch
        {
            H26xQualityOptions h26x => new JsonObject
            {
                ["codec"] = EnumToString(h26x.Codec),
                ["crf"] = h26x.Crf,
                ["preset"] = h26x.Preset,
                ["profile"] = h26x.Profile,
            },
            Vp9QualityOptions vp9 => new JsonObject
            {
                ["codec"] = EnumToString(vp9.Codec),
                ["quality"] = vp9.Quality,
                ["two_pass"] = vp9.TwoPass,
            },
            Av1QualityOptions av1 => new JsonObject
            {
                ["codec"] = EnumToString(av1.Codec),
                ["crf"] = av1.Crf,
                ["preset"] = av1.Preset,
                ["tune"] = av1.Tune,
                ["enable_overlays"] = av1.EnableOverlays,
                ["scm"] = av1.Scm,
                ["max_bitrate_kbps"] = av1.MaxBitrateKbps,
                ["enable_variance_boost"] = av1.EnableVarianceBoost,
            },
            _ => throw new InvalidOperationException(
                $"Unknown QualityOptions type: {options.GetType().Name}"),
        };
    }

    private static VideoEncoderOptions DeserializeVideoEncoderOptions(
        VideoCodec videoCodec, JsonElement element)
    {
        if (videoCodec is VideoCodec.H264 or VideoCodec.H265)
        {
            var defaults = H26xEncoderOptions.ForCodec(videoCodec);
            var preset = ReadString(element, "preset", defaults.Preset);
            var profile = ReadString(element, "profile", defaults.Profile);
            var label = CompatibilityData.VideoCodecLabel(videoCodec);
            if (!CompatibilityData.QualityPresetOptions(videoCodec).Contains(preset))
                throw new SettingsDecodeException($"Invalid {label} encoder preset.");
            if (!CompatibilityData.QualityProfileOptions(videoCodec).Contains(profile))
                throw new SettingsDecodeException($"Invalid {label} encoder profile.");
            return new H26xEncoderOptions(
                Codec: videoCodec, Preset: preset, Profile: profile,
                AdditionalOptions: ReadString(element, "additional_options", ""));
        }

        if (videoCodec == VideoCodec.Av1)
        {
            var preset = ReadInt(element, "preset", 6);
            var tune = ReadInt(element, "tune", 0);
            var enableOverlays = ReadInt(element, "enable_overlays", 1);
            var scm = ReadInt(element, "scm", 2);
            var enableVarianceBoost = ReadInt(element, "enable_variance_boost", 0);
            return new Av1EncoderOptions(
                Preset: preset, Tune: tune,
                EnableOverlays: enableOverlays, Scm: scm,
                EnableVarianceBoost: enableVarianceBoost,
                AdditionalOptions: ReadString(element, "additional_options", ""));
        }

        // VP9
        var cpuUsed = ReadInt(element, "cpu_used", 2);
        if (cpuUsed < CompatibilityData.VpxSpeedMin || cpuUsed > CompatibilityData.VpxSpeedMax)
            throw new SettingsDecodeException("Invalid VP9 CPU used setting.");
        var tileColumns = ReadInt(element, "tile_columns", 0);
        if (tileColumns < CompatibilityData.VpxTileColumnsMin || tileColumns > CompatibilityData.VpxTileColumnsMax)
            throw new SettingsDecodeException("Invalid VP9 tile columns setting.");
        var tileRows = ReadInt(element, "tile_rows", 0);
        if (tileRows < CompatibilityData.VpxTileRowsMin || tileRows > CompatibilityData.VpxTileRowsMax)
            throw new SettingsDecodeException("Invalid VP9 tile rows setting.");
        return new VpxEncoderOptions(
            videoCodec, CpuUsed: cpuUsed, TileColumns: tileColumns, TileRows: tileRows,
            AdditionalOptions: ReadString(element, "additional_options", ""));
    }

    private static VideoEncoderOptions DefaultVideoEncoderOptions(
        VideoCodec videoCodec,
        QualityOptions targetQuality,
        JsonElement qualityElement)
    {
        return videoCodec switch
        {
            VideoCodec.H264 or VideoCodec.H265 when targetQuality is H26xQualityOptions h26x =>
                new H26xEncoderOptions(
                    Codec: videoCodec,
                    Preset: h26x.Preset,
                    Profile: h26x.Profile),

            VideoCodec.Av1 when targetQuality is Av1QualityOptions av1 =>
                new Av1EncoderOptions(
                    Preset: av1.Preset,
                    Tune: av1.Tune,
                    EnableOverlays: av1.EnableOverlays,
                    Scm: av1.Scm,
                    EnableVarianceBoost: av1.EnableVarianceBoost),

            VideoCodec.Vp9 => DefaultVpxEncoderOptions(videoCodec, qualityElement),

            _ => throw new SettingsDecodeException("Unsupported video encoder options."),
        };
    }

    private static VpxEncoderOptions DefaultVpxEncoderOptions(
        VideoCodec videoCodec,
        JsonElement qualityElement)
    {
        var cpuUsed = ReadInt(qualityElement, "speed", 2);
        if (cpuUsed < CompatibilityData.VpxSpeedMin || cpuUsed > CompatibilityData.VpxSpeedMax)
            throw new SettingsDecodeException("Invalid VP9 CPU used setting.");
        return new VpxEncoderOptions(videoCodec, CpuUsed: cpuUsed);
    }

    private static QualityOptions DeserializeQualityOptions(
        VideoCodec videoCodec, JsonElement element)
    {
        if (videoCodec is VideoCodec.H264 or VideoCodec.H265)
        {
            var defaults = H26xQualityOptions.ForCodec(videoCodec);
            var preset = ReadString(element, "preset", defaults.Preset);
            var profile = ReadString(element, "profile", defaults.Profile);
            var label = CompatibilityData.VideoCodecLabel(videoCodec);
            if (!CompatibilityData.QualityPresetOptions(videoCodec).Contains(preset))
                throw new SettingsDecodeException($"Invalid {label} quality preset.");
            if (!CompatibilityData.QualityProfileOptions(videoCodec).Contains(profile))
                throw new SettingsDecodeException($"Invalid {label} quality profile.");
            return new H26xQualityOptions(
                Codec: videoCodec,
                Crf: ReadInt(element, "crf", defaults.Crf),
                Preset: preset,
                Profile: profile);
        }

        if (videoCodec == VideoCodec.Av1)
        {
            return new Av1QualityOptions(
                Crf: ReadInt(element, "crf", 30),
                Preset: ReadInt(element, "preset", 6),
                Tune: ReadInt(element, "tune", 0),
                EnableOverlays: ReadInt(element, "enable_overlays", 1),
                Scm: ReadInt(element, "scm", 2),
                MaxBitrateKbps: ReadInt(element, "max_bitrate_kbps", 0),
                EnableVarianceBoost: ReadInt(element, "enable_variance_boost", 0));
        }

        return new Vp9QualityOptions(
            Quality: ReadInt(element, "quality", 32),
            TwoPass: ReadBool(element, "two_pass"));
    }

    // --- Validation helpers ---

    private static void ValidateContainerCodecs(
        OutputContainer container, VideoCodec videoCodec)
    {
        var allowed = CompatibilityData.AllowedVideoCodecs(container);
        if (allowed.Count == 0 || !allowed.Contains(videoCodec))
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

    internal static JsonElement GetObjectOrEmpty(JsonElement parent, string propertyName)
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

    internal static int? ReadOptionalInt(JsonElement element, string property)
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

    internal static bool ReadBool(
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
