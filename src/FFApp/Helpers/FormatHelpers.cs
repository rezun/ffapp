using System.Text;
using FFApp.Core;
using FFApp.Core.Models;

namespace FFApp.Helpers;

public static class FormatHelpers
{
    public static string BuildMediaSummaryText(SourceMediaInfo mediaInfo)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Source: {mediaInfo.SourcePath}");
        sb.AppendLine($"Format: {mediaInfo.ContainerFormat ?? "Unknown"}");
        sb.AppendLine($"Duration: {FormatDuration(mediaInfo.DurationSeconds)}");
        sb.AppendLine($"Overall bitrate: {FormatBitrate(mediaInfo.BitRate)}");
        sb.AppendLine();
        sb.AppendLine($"Video streams: {mediaInfo.VideoStreams.Length}");
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

    public static string FormatDependencyStatus(DependencyStatus status)
    {
        return $"{FormatSingleDependency(status.Ffmpeg)}\n{FormatSingleDependency(status.Ffprobe)}";
    }

    public static string FormatValidationResult(ValidationResult result)
    {
        if (result.Issues.Length == 0)
            return "Current settings are valid for the loaded source.";

        var lines = new List<string>();
        if (result.HasErrors)
        {
            lines.Add("Errors");
            lines.AddRange(result.Errors.Select(i => $"- {i.Message}"));
        }
        if (result.HasWarnings)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add("Warnings");
            lines.AddRange(result.Warnings.Select(i => $"- {i.Message}"));
        }
        return string.Join('\n', lines);
    }

    public static string FormatCommandPreviewText(IReadOnlyList<GeneratedCommand> commands, bool singleCommand = false)
    {
        if (commands.Count == 0) return "";
        if (commands.Count == 1) return commands[0].DisplayString;
        if (singleCommand)
            return string.Join(" && ", commands.Select(c => c.DisplayString));

        var sections = new List<string>();
        for (int i = 0; i < commands.Count; i++)
        {
            sections.Add($"Pass {i + 1}");
            sections.Add(commands[i].DisplayString);
        }
        return string.Join("\n\n", sections);
    }

    public static string FormatJobProgress(JobProgress progress) =>
        string.Join(" | ",
            $"State: {JobStateLabel(progress.State)}",
            $"Pass: {progress.PassIndex} of {progress.PassCount}",
            $"Elapsed: {FormatPreciseDuration(progress.ElapsedSeconds)}",
            $"Processed: {FormatPreciseDuration(progress.ProcessedSeconds)}",
            $"Progress: {FormatPercentage(progress.Percentage)}",
            $"Speed: {FormatSpeed(progress.Speed)}");

    public static string FormatJobResultSummary(JobResult result) =>
        $"State: {JobStateLabel(result.State)} | " +
        $"Elapsed: {FormatPreciseDuration(result.ElapsedSeconds)} | " +
        $"Commands Run: {result.ProcessResults.Length} of {result.Commands.Length}";

    public static string FormatQueueSummary(IEnumerable<QueueItem> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return "Queue is empty.";

        var counts = Enum.GetValues<JobState>()
            .ToDictionary(s => s, s => list.Count(i => i.State == s));

        return $"Items: {list.Count} | " +
               $"Pending: {counts[JobState.Pending]} | " +
               $"Running: {counts[JobState.Running]} | " +
               $"Completed: {counts[JobState.Completed]} | " +
               $"Failed: {counts[JobState.Failed]} | " +
               $"Canceled: {counts[JobState.Canceled]}";
    }

    public static string FormatQueueItemLabel(QueueItem item)
    {
        var progress = "";
        if (item.Progress?.Percentage is { } pct)
            progress = $" | {FormatPercentage(pct)}";

        return $"{Path.GetFileName(item.QueuedJob.Job.SourcePath)} -> " +
               $"{Path.GetFileName(item.QueuedJob.Job.OutputPath)} | " +
               $"{JobStateLabel(item.State)}{progress}";
    }

    public static string FormatQueueItemDetails(QueueItem item)
    {
        var lines = new List<string>
        {
            $"Source: {item.QueuedJob.Job.SourcePath}",
            $"Output: {item.QueuedJob.Job.OutputPath}",
            $"State: {JobStateLabel(item.State)}",
            $"Passes: {item.QueuedJob.Commands.Length}"
        };

        if (item.Progress is not null)
            lines.Add(FormatJobProgress(item.Progress));
        else if (item.Result is not null)
            lines.Add(FormatJobResultSummary(item.Result));

        if (item.QueuedJob.Validation.HasWarnings)
            lines.Add($"Warnings: {item.QueuedJob.Validation.Warnings.Length}");
        if (item.FailureMessage is not null)
            lines.Add($"Failure: {item.FailureMessage}");

        return string.Join('\n', lines);
    }

    public static JobDefinition BuildJobDefinition(JobContext context)
    {
        var outputPath = DefaultCommandBuilder.SuggestOutputPath(
            context.MediaInfo.SourcePath, context.Settings.Container,
            context.OutputDirectory, context.OutputFilename, context.IsAutoName);

        return BuildJobDefinition(
            context.MediaInfo.SourcePath, outputPath, context.Settings,
            context.TrimStartSeconds, context.TrimEndSeconds, context.Crop);
    }

    public static string ResolveOutputPath(JobContext context) =>
        DefaultCommandBuilder.SuggestOutputPath(
            context.MediaInfo.SourcePath, context.Settings.Container,
            context.OutputDirectory, context.OutputFilename, context.IsAutoName);

    public static JobDefinition BuildJobDefinition(
        string sourcePath, string outputPath, OutputSettings settings,
        double? trimStartSeconds = null, double? trimEndSeconds = null,
        CropRect? crop = null) =>
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
            HdrMode: settings.HdrMode,
            ToneMapSettings: settings.ToneMapSettings);

    public static string SuggestQueueOutputPath(string sourcePath, OutputContainer container, HashSet<string> reservedOutputs)
    {
        var ext = DefaultCommandBuilder.ContainerExtension(container);
        var candidate = Path.ChangeExtension(sourcePath, ext);
        if (!File.Exists(candidate) && !reservedOutputs.Contains(Path.GetFullPath(candidate)))
            return candidate;

        var dir = Path.GetDirectoryName(candidate) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(candidate);
        var suffix = Path.GetExtension(candidate);
        for (int counter = 1; counter <= 10_000; counter++)
        {
            var numbered = Path.Combine(dir, $"{stem}-{counter}{suffix}");
            if (!File.Exists(numbered) && !reservedOutputs.Contains(Path.GetFullPath(numbered)))
                return numbered;
        }

        throw new InvalidOperationException(
            $"Could not find an available output filename after 10,000 attempts for '{stem}{suffix}'.");
    }

    public static string VideoCodecLabel(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.H265 => "H.265",
        VideoCodec.Vp8 => "VP8",
        VideoCodec.Vp9 => "VP9",
        VideoCodec.Av1 => "AV1",
        _ => codec.ToString()
    };

    public static string AudioCodecLabel(AudioCodec codec) => codec switch
    {
        AudioCodec.Aac => "AAC",
        AudioCodec.Mp3 => "MP3",
        AudioCodec.Vorbis => "Vorbis",
        AudioCodec.Opus => "Opus",
        _ => codec.ToString()
    };

    public static string JobStateLabel(JobState state) => state switch
    {
        JobState.Pending => "Pending",
        JobState.Running => "Running",
        JobState.Completed => "Completed",
        JobState.Failed => "Failed",
        JobState.Canceled => "Canceled",
        _ => state.ToString()
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

    private static string FormatSingleDependency(ExecutableValidation validation)
    {
        if (validation.IsValid)
        {
            var version = validation.VersionOutput?.Split('\n', 2)[0] ?? "Validated";
            return $"{validation.Name}: ready at {validation.Path}\n{version}";
        }
        return $"{validation.Name}: {validation.Error ?? "Not configured"}";
    }

    private static string FormatDuration(double? seconds)
    {
        if (seconds is null) return "Unknown";
        var ts = TimeSpan.FromSeconds(seconds.Value);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private static string FormatPreciseDuration(double? seconds)
    {
        if (seconds is null) return "Unknown";
        var ts = TimeSpan.FromSeconds(seconds.Value);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100}";
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

    private static string FormatPercentage(double? value) =>
        value is null ? "Unknown" : $"{value:F1}%";

    private static string FormatSpeed(double? value) =>
        value is null ? "Unknown" : $"{value:F2}x";
}
