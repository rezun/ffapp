using System.Collections.Immutable;
using System.Text;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Validation;

namespace FFApp.Helpers;

public static class FormatHelpers
{
    public static string FormatStorageSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):F1} GiB",
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):F1} MiB",
        >= 1024 => $"{bytes / 1024d:F1} KiB",
        _ => $"{bytes} B",
    };

    public static string BuildMediaSummaryText(SourceMediaInfo mediaInfo)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Source: {mediaInfo.SourcePath}");
        sb.AppendLine($"Format: {mediaInfo.ContainerFormat ?? "Unknown"}");
        sb.AppendLine($"Duration: {FormatDuration(mediaInfo.DurationSeconds)}");
        sb.AppendLine($"Overall bitrate: {FormatBitrate(mediaInfo.BitRate)}");
        sb.AppendLine();
        sb.AppendLine($"Video streams: {mediaInfo.VideoStreams.Length}");
        if (mediaInfo.AttachedPictureStreams.Length > 0)
            sb.AppendLine($"Attached pictures: {mediaInfo.AttachedPictureStreams.Length}");
        sb.AppendLine($"Audio streams: {mediaInfo.AudioStreams.Length}");
        sb.AppendLine($"Subtitle streams: {mediaInfo.SubtitleStreams.Length}");
        sb.AppendLine($"Data streams: {mediaInfo.DataStreams.Length}");

        var primaryVideo = mediaInfo.PrimaryVideoStream;
        if (primaryVideo is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Primary video stream");
            sb.AppendLine($"Codec: {primaryVideo.CodecName ?? "Unknown"}");
            sb.AppendLine($"Resolution: {FormatDimensions(primaryVideo.Width, primaryVideo.Height)}");
            sb.AppendLine($"Frame rate: {FormatFrameRate(primaryVideo.FrameRate)}");
            sb.AppendLine($"Bitrate: {FormatBitrate(primaryVideo.BitRate)}");
            if (primaryVideo.IsHdr)
            {
                var hdrLabel = HdrFormatLabel(primaryVideo.HdrFormat);
                var bitDepth = primaryVideo.BitsPerRawSample is not null
                    ? $"{primaryVideo.BitsPerRawSample}-bit" : "10-bit";
                var colorInfo = $"{primaryVideo.ColorPrimaries ?? "bt2020"} / {TransferFunctionLabel(primaryVideo.ColorTransfer)}";
                sb.AppendLine($"HDR: {hdrLabel} ({bitDepth}, {colorInfo})");

                if (primaryVideo.HdrMetadata is { } hdrMeta)
                {
                    if (hdrMeta.MaxContentLightLevel is not null || hdrMeta.MaxAverageLightLevel is not null)
                        sb.AppendLine($"Peak brightness: {hdrMeta.MaxContentLightLevel ?? 0} nits MaxCLL, {hdrMeta.MaxAverageLightLevel ?? 0} nits MaxFALL");
                }
            }
        }

        if (mediaInfo.AudioStreams.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Audio streams detail");
            foreach (var stream in mediaInfo.AudioStreams)
            {
                sb.AppendLine($"Track {stream.Index}");
                sb.AppendLine($"Codec: {stream.CodecName ?? "Unknown"}");
                sb.AppendLine($"Language: {stream.Language ?? "Unknown"}");
                sb.AppendLine($"Channels: {FormatChannels(stream.Channels)}");
                sb.AppendLine($"Bitrate: {FormatAudioBitrate(stream.BitRate)}");
            }
        }

        if (mediaInfo.VideoStreams.Length > 1)
        {
            sb.AppendLine();
            sb.AppendLine("Warning: multiple video streams detected. MVP uses the first stream only.");
        }

        return sb.ToString().TrimEnd();
    }

    public static JobDefinition BuildJobDefinition(
        JobContext context,
        IReadOnlyCollection<string>? reservedOutputPaths = null)
    {
        var outputPath = SuggestContextOutputPath(context, reservedOutputPaths);

        return BuildJobDefinition(
            context.MediaInfo.SourcePath, outputPath, context.Settings,
            context.TrimStartSeconds, context.TrimEndSeconds, context.Crop,
            context.SelectedAudioStreamIndices, context.SelectedSubtitleStreamIndices);
    }

    public static string ResolveOutputPath(JobContext context) =>
        SuggestContextOutputPath(context, reservedOutputPaths: null);

    private static string SuggestContextOutputPath(
        JobContext context, IReadOnlyCollection<string>? reservedOutputPaths) =>
        DefaultCommandBuilder.SuggestOutputPath(
            context.MediaInfo.SourcePath, context.Settings.Container,
            context.OutputDirectory,
            OutputNaming.AutoStem(context.MediaInfo.SourcePath, context.OutputPresetName),
            isAutoName: true, reservedOutputPaths);

    public static JobDefinition BuildJobDefinition(
        string sourcePath, string outputPath, OutputSettings settings,
        double? trimStartSeconds = null, double? trimEndSeconds = null,
        CropRect? crop = null, ImmutableArray<int>? selectedAudioStreamIndices = null,
        ImmutableArray<int>? selectedSubtitleStreamIndices = null) =>
        new(
            SourcePath: sourcePath,
            OutputPath: outputPath,
            Container: settings.Container,
            VideoMode: settings.VideoMode,
            VideoCodec: settings.VideoCodec,
            EncodingStrategy: settings.EncodingStrategy,
            VideoEncoderOptions: settings.VideoEncoderOptions,
            AudioOptions: settings.AudioOptions,
            StreamHandling: settings.StreamHandling,
            ResolutionPreset: settings.ResolutionPreset,
            FrameRatePreset: settings.FrameRatePreset,
            TargetFileSize: settings.TargetFileSize,
            TargetBitrate: settings.TargetBitrate,
            TargetQuality: settings.TargetQuality,
            TrimStartSeconds: trimStartSeconds,
            TrimEndSeconds: trimEndSeconds,
            Crop: crop,
            ColorMetadata: settings.ColorMetadata,
            HdrMode: settings.HdrMode,
            ToneMapSettings: settings.ToneMapSettings,
            KeyframeInterval: settings.KeyframeInterval,
            KeyframeUnit: settings.KeyframeUnit,
            PostProcessing: settings.PostProcessing,
            SelectedAudioStreamIndices: selectedAudioStreamIndices,
            SelectedSubtitleStreamIndices: selectedSubtitleStreamIndices);

    public static string VideoCodecLabel(VideoCodec codec) =>
        CompatibilityData.VideoCodecLabel(codec);

    public static string AudioCodecLabel(AudioCodec codec) => codec switch
    {
        AudioCodec.Aac => "AAC",
        AudioCodec.Mp3 => "MP3",
        AudioCodec.Vorbis => "Vorbis",
        AudioCodec.Opus => "Opus",
        _ => codec.ToString()
    };

    public static string ContainerLabel(OutputContainer container) => container switch
    {
        OutputContainer.Mp4 => "MP4",
        OutputContainer.Mkv => "MKV",
        OutputContainer.Webm => "WEBM",
        OutputContainer.Ogg => "OGG",
        OutputContainer.M4a => "M4A",
        OutputContainer.Mka => "MKA",
        OutputContainer.OpusFile => "Opus",
        _ => container.ToString()
    };

    public static string VideoModeLabel(VideoMode mode) => mode switch
    {
        VideoMode.Copy => "Copy",
        VideoMode.Reencode => "Re-encode",
        VideoMode.Remove => "Remove",
        _ => mode.ToString()
    };

    public static string ResolutionPresetLabel(ResolutionPreset preset) => preset switch
    {
        ResolutionPreset.Original => "Original",
        ResolutionPreset.P720 => "720p",
        ResolutionPreset.P1080 => "1080p",
        ResolutionPreset.P1440 => "1440p",
        ResolutionPreset.P4K => "4K",
        _ => preset.ToString()
    };

    public static string FrameRatePresetLabel(FrameRatePreset preset) => preset switch
    {
        FrameRatePreset.Original => "Original",
        FrameRatePreset.Fps23_976 => "23.976 fps",
        FrameRatePreset.Fps24 => "24 fps",
        FrameRatePreset.Fps25 => "25 fps",
        FrameRatePreset.Fps29_97 => "29.97 fps",
        FrameRatePreset.Fps30 => "30 fps",
        FrameRatePreset.Fps50 => "50 fps",
        FrameRatePreset.Fps59_94 => "59.94 fps",
        FrameRatePreset.Fps60 => "60 fps",
        _ => preset.ToString()
    };

    public static string HdrModeLabel(HdrMode mode) => mode switch
    {
        HdrMode.Preserve => "Preserve HDR",
        HdrMode.TonemapToSdr => "Convert to SDR",
        _ => mode.ToString(),
    };

    public static string ToneMapAlgorithmLabel(ToneMapAlgorithm algorithm) => algorithm switch
    {
        ToneMapAlgorithm.Hable => "Hable (filmic)",
        ToneMapAlgorithm.Reinhard => "Reinhard",
        ToneMapAlgorithm.Mobius => "Mobius",
        ToneMapAlgorithm.Bt2390 => "BT.2390 (broadcast)",
        ToneMapAlgorithm.Clip => "Clip (hard)",
        _ => algorithm.ToString(),
    };

    public static string ToneMapEngineLabel(ToneMapEngine engine) => engine switch
    {
        ToneMapEngine.Standard => "Standard",
        ToneMapEngine.Libplacebo => "libplacebo",
        _ => engine.ToString(),
    };

    public static string LibplaceboToneMapAlgorithmLabel(LibplaceboToneMapAlgorithm algorithm) => algorithm switch
    {
        LibplaceboToneMapAlgorithm.Auto => "Auto",
        LibplaceboToneMapAlgorithm.Clip => "Clip (hard)",
        LibplaceboToneMapAlgorithm.Bt2446a => "BT.2446a",
        LibplaceboToneMapAlgorithm.Bt2446c => "BT.2446c",
        LibplaceboToneMapAlgorithm.Spline => "Spline",
        LibplaceboToneMapAlgorithm.Reinhard => "Reinhard",
        LibplaceboToneMapAlgorithm.Hable => "Hable (filmic)",
        LibplaceboToneMapAlgorithm.Mobius => "Mobius",
        LibplaceboToneMapAlgorithm.St2094_40 => "ST 2094-40",
        LibplaceboToneMapAlgorithm.St2094_10 => "ST 2094-10",
        _ => algorithm.ToString(),
    };

    public static string GamutModeLabel(GamutMode mode) => mode switch
    {
        GamutMode.Clip => "Clip",
        GamutMode.Perceptual => "Perceptual",
        GamutMode.Relative => "Relative",
        GamutMode.Saturation => "Saturation",
        GamutMode.Absolute => "Absolute",
        GamutMode.Desaturate => "Desaturate",
        GamutMode.Darken => "Darken",
        GamutMode.Linear => "Linear",
        _ => mode.ToString(),
    };

    public static string HdrFormatLabel(HdrFormat format) => format switch
    {
        HdrFormat.Hdr10 => "HDR10",
        HdrFormat.Hlg => "HLG",
        HdrFormat.Sdr => "SDR",
        _ => format.ToString(),
    };

    public static string TransferFunctionLabel(string? transfer) => transfer switch
    {
        "smpte2084" => "PQ",
        "arib-std-b67" => "HLG",
        "bt709" => "BT.709",
        _ => transfer ?? "Unknown",
    };

    /// <summary>
    /// Formats seconds as compact duration: "2:30" (under 1h) or "1:23:45" (1h+).
    /// </summary>
    public static string FormatCompactDuration(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    /// <summary>
    /// Estimates remaining seconds from elapsed time and percentage complete.
    /// Returns null when there isn't enough data for a meaningful estimate.
    /// </summary>
    public static double? EstimateRemainingSeconds(double elapsedSeconds, double? percentage)
    {
        if (percentage is not > 1 || percentage >= 100 || elapsedSeconds < 3)
            return null;
        return elapsedSeconds * (100.0 - percentage.Value) / percentage.Value;
    }

    // --- Private helpers ---

    private static string FormatDuration(double? seconds)
    {
        if (seconds is null) return "Unknown";
        var ts = TimeSpan.FromSeconds(seconds.Value);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private static string FormatBitrate(int? bitRate) =>
        bitRate is null ? "Unknown" : $"{bitRate.Value / 1_000_000.0:F2} Mbps";

    private static string FormatAudioBitrate(int? bitRate) =>
        bitRate is null ? "Unknown" : $"{bitRate.Value / 1_000.0:F0} kbps";

    private static string FormatDimensions(int? width, int? height) =>
        width is null || height is null ? "Unknown" : $"{width}x{height}";

    private static string FormatFrameRate(double? frameRate) =>
        frameRate is null ? "Unknown" : $"{frameRate:F2} fps";

    private static string FormatChannels(int? channels) => channels switch
    {
        null => "Unknown",
        1 => "1 channel",
        _ => $"{channels} channels"
    };
}
