using System.Text.Json.Nodes;
using FFApp.Core.Models;
using FFApp.Infrastructure.Serialization;

namespace FFApp.Helpers;

public static class ComparisonSettingsDescription
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["container"] = "Container", ["video_codec"] = "Video codec", ["encoding_strategy"] = "Strategy",
        ["resolution_preset"] = "Resolution", ["frame_rate_preset"] = "Frame rate",
        ["video_encoder_options"] = "Encoder settings", ["target_quality"] = "Quality settings",
        ["audio_options.mode"] = "Audio mode", ["audio_options.codec"] = "Audio codec",
        ["audio_options.bitrate"] = "Audio bitrate", ["audio_options.audio_channels"] = "Audio channels",
        ["audio_options.sample_rate"] = "Audio sample rate", ["remove_track_titles"] = "Remove audio track titles",
        ["remove_subtitles"] = "Remove subtitles", ["remove_data"] = "Remove data", ["remove_images"] = "Remove images",
        ["target_size_mb"] = "Target file size (MB)", ["two_pass"] = "Two-pass encoding",
        ["muxing_overhead_ratio"] = "Muxing overhead ratio", ["absolute_overhead_bytes"] = "Minimum muxing overhead (bytes)",
        ["video_bitrate"] = "Video bitrate", ["crf"] = "CRF", ["quality"] = "Quality",
        ["preset"] = "Preset", ["profile"] = "Profile", ["tune"] = "Tune", ["cpu_used"] = "Speed",
        ["tile_columns"] = "Tile columns", ["tile_rows"] = "Tile rows", ["additional_options"] = "Additional encoder options",
        ["enable_overlays"] = "Overlays", ["scm"] = "Screen content mode", ["max_bitrate_kbps"] = "Maximum video bitrate (kbps)",
        ["enable_variance_boost"] = "Variance boost", ["variance_boost_strength"] = "Variance boost strength", ["variance_octile"] = "Variance octile",
        ["film_grain"] = "Film grain", ["film_grain_denoise"] = "Source denoising", ["adaptive_film_grain"] = "Adaptive film grain",
        ["hdr_mode"] = "HDR mode", ["engine"] = "Tone-map engine", ["algorithm"] = "Tone-map algorithm",
        ["desaturation"] = "Desaturation", ["peak_nits"] = "Source peak (nits)", ["libplacebo_algorithm"] = "libplacebo algorithm",
        ["gamut_mode"] = "Gamut mapping", ["saturation"] = "Saturation", ["contrast_recovery"] = "Contrast recovery",
        ["peak_detect"] = "Peak detection", ["tonemapping_param"] = "Tone-map parameter",
        ["keyframe_interval"] = "Keyframe interval", ["keyframe_unit"] = "Keyframe unit",
        ["recalculate_matroska_statistics"] = "Recalculate Matroska statistics",
        ["color_metadata.mode"] = "Color labels", ["primaries"] = "Color primaries", ["transfer"] = "Transfer function",
        ["matrix"] = "YUV matrix", ["range"] = "Signal range",
    };

    public static string Differences(OutputSettings baseline, OutputSettings selected)
    {
        var lines = new List<string>();
        Compare(SettingsSerializer.SerializeOutputSettings(baseline), SettingsSerializer.SerializeOutputSettings(selected), "", lines);
        return lines.Count == 0 ? "Matches the initial settings."
            : string.Join(Environment.NewLine, lines.Distinct(StringComparer.Ordinal));
    }

    private static void Compare(JsonNode? before, JsonNode? after, string path, List<string> lines)
    {
        if (JsonNode.DeepEquals(before, after)) return;
        if (before is JsonObject first && after is JsonObject second)
        {
            foreach (var key in first.Select(p => p.Key).Union(second.Select(p => p.Key), StringComparer.Ordinal))
                Compare(first[key], second[key], path.Length == 0 ? key : path + "." + key, lines);
            return;
        }
        var leaf = path[(path.LastIndexOf('.') + 1)..];
        var label = Labels.TryGetValue(path, out var fullLabel) ? fullLabel
            : Labels.TryGetValue(leaf, out var shortLabel) ? shortLabel : leaf.Replace('_', ' ');
        lines.Add($"{label}: {Display(before)} → {Display(after)}");
    }

    private static string Display(JsonNode? node)
    {
        if (node is null) return "Auto";
        if (node is JsonObject) return "Custom";
        if (node is JsonValue value && value.TryGetValue<bool>(out var boolean)) return boolean ? "On" : "Off";
        var text = node.ToString();
        return text switch
        {
            "" => "Default", "target_quality" => "Quality", "target_file_size" => "File size", "target_bitrate" => "Bitrate",
            "tonemap_to_sdr" => "Convert to SDR", "preserve" => "Preserve HDR", "reencode" => "Re-encode",
            _ => text.Replace('_', ' '),
        };
    }

    public static OutputSettings NormalizeInactiveGrainDenoising(OutputSettings settings) => settings with
    {
        TargetQuality = settings.TargetQuality is Av1QualityOptions { FilmGrain: 0 } quality
            ? quality with { FilmGrainDenoise = 0 } : settings.TargetQuality,
        VideoEncoderOptions = settings.VideoEncoderOptions is Av1EncoderOptions { FilmGrain: 0 } encoder
            ? encoder with { FilmGrainDenoise = 0 } : settings.VideoEncoderOptions,
    };
}
