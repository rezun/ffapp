using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;

namespace FFApp.Core;

public sealed class CommandBuildException(string message)
    : InvalidOperationException(message);

public sealed class DefaultCommandBuilder(ExecutablePathsConfig pathsConfig) : ICommandBuilder
{
    private static readonly string NullOutputPath =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "NUL" : "/dev/null";

    public IReadOnlyList<GeneratedCommand> Build(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var inputArgs = new List<string> { "-hide_banner", "-y" };
        var trimStart = job.TrimStartSeconds ?? 0;
        if (trimStart > 0)
            inputArgs.AddRange(["-ss", FormatSeconds(trimStart)]);
        inputArgs.AddRange(["-i", job.SourcePath]);
        if (job.TrimEndSeconds is > 0 && job.TrimEndSeconds < (mediaInfo.DurationSeconds ?? double.MaxValue))
        {
            // -to is relative to the output start (after -ss), so use duration, not absolute time
            var duration = job.TrimEndSeconds.Value - trimStart;
            if (duration > 0)
                inputArgs.AddRange(["-t", FormatSeconds(duration)]);
        }
        var mappingArgs = BuildMappingArguments(job, mediaInfo);
        var streamArgs = BuildStreamArguments(job, mediaInfo);

        // Audio-only: no video encoding, no two-pass
        if (job.VideoMode == VideoMode.Remove)
        {
            var arguments = new List<string>(inputArgs);
            arguments.AddRange(mappingArgs);
            arguments.AddRange(streamArgs);
            arguments.AddRange(["-vn"]);
            if (job.Container is OutputContainer.Mp4 or OutputContainer.M4a)
                arguments.AddRange(["-movflags", "+faststart"]);
            arguments.Add(job.OutputPath);
            return [CreateGeneratedCommand(pathsConfig.FfmpegPath, arguments)];
        }

        // Video copy: stream-copy video, no filters/encoding
        if (job.VideoMode == VideoMode.Copy)
        {
            var arguments = new List<string>(inputArgs);
            arguments.AddRange(mappingArgs);
            arguments.AddRange(streamArgs);
            arguments.AddRange(["-c:v", "copy"]);
            if (job.Container is OutputContainer.Mp4 or OutputContainer.M4a)
                arguments.AddRange(["-movflags", "+faststart"]);
            arguments.Add(job.OutputPath);
            return [CreateGeneratedCommand(pathsConfig.FfmpegPath, arguments)];
        }

        // Video re-encode (original path)
        if (UsesTwoPass(job))
        {
            int? bitrate = null;
            if (job.EncodingStrategy != EncodingStrategy.TargetQuality)
                bitrate = ResolveVideoBitrate(job, mediaInfo);

            var passLogPath = PassLogPath(job.OutputPath);

            var firstPassArgs = new List<string>(inputArgs);
            firstPassArgs.AddRange(mappingArgs);
            firstPassArgs.AddRange(BuildVideoCodecArguments(job, mediaInfo, bitrate: bitrate, passIndex: 1));
            firstPassArgs.AddRange(["-pass", "1", "-passlogfile", passLogPath, "-an", "-sn", "-dn", "-f", "null", NullOutputPath]);

            var secondPassArgs = new List<string>(inputArgs);
            secondPassArgs.AddRange(mappingArgs);
            secondPassArgs.AddRange(streamArgs);
            secondPassArgs.AddRange(BuildVideoCodecArguments(job, mediaInfo, bitrate: bitrate, passIndex: 2));
            secondPassArgs.AddRange(["-pass", "2", "-passlogfile", passLogPath]);
            if (job.Container == OutputContainer.Mp4)
                secondPassArgs.AddRange(["-movflags", "+faststart"]);
            secondPassArgs.Add(job.OutputPath);

            return
            [
                CreateGeneratedCommand(pathsConfig.FfmpegPath, firstPassArgs),
                CreateGeneratedCommand(pathsConfig.FfmpegPath, secondPassArgs),
            ];
        }

        var reencodeArgs = new List<string>(inputArgs);
        reencodeArgs.AddRange(mappingArgs);
        reencodeArgs.AddRange(streamArgs);
        reencodeArgs.AddRange(BuildVideoCodecArguments(job, mediaInfo));
        if (job.Container == OutputContainer.Mp4)
            reencodeArgs.AddRange(["-movflags", "+faststart"]);
        reencodeArgs.Add(job.OutputPath);

        return [CreateGeneratedCommand(pathsConfig.FfmpegPath, reencodeArgs)];
    }

    public static string SuggestOutputPath(
        string sourcePath, OutputContainer container,
        string? outputDirectory = null, string? outputFilename = null,
        bool isAutoName = false)
    {
        var extension = ContainerExtension(container);
        var directory = outputDirectory ?? Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var stem = outputFilename ?? Path.GetFileNameWithoutExtension(sourcePath);
        var candidate = Path.Combine(directory, stem + extension);

        // When the user explicitly chose a filename, use it as-is (ffmpeg -y handles overwrites)
        if (!isAutoName)
            return candidate;

        if (!File.Exists(candidate))
            return candidate;

        for (var counter = 1; counter <= 10_000; counter++)
        {
            var numbered = Path.Combine(directory, $"{stem}-{counter}{extension}");
            if (!File.Exists(numbered))
                return numbered;
        }

        throw new CommandBuildException(
            "Could not find an available output filename after 10,000 attempts.");
    }

    // --- Private helpers ---

    private static GeneratedCommand CreateGeneratedCommand(string executable, List<string> arguments)
    {
        var displayParts = new List<string>(arguments.Count + 1) { QuoteArgument(executable) };
        foreach (var arg in arguments)
            displayParts.Add(QuoteArgument(arg));

        return new GeneratedCommand(
            ExecutablePath: executable,
            Arguments: [.. arguments],
            DisplayString: string.Join(' ', displayParts));
    }

    private static readonly SearchValues<char> ShellMetaCharacters =
        SearchValues.Create(" \t\"'\\()&|;<>$`!{}");

    private static string QuoteArgument(string arg)
    {
        if (arg.Length == 0)
            return "''";
        if (arg.AsSpan().ContainsAny(ShellMetaCharacters))
            return "'" + arg.Replace("'", "'\"'\"'") + "'";
        return arg;
    }

    private static List<string> BuildMappingArguments(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var arguments = new List<string> { "-map", "0" };

        if (job.VideoMode == VideoMode.Remove)
        {
            arguments.AddRange(["-map", "-0:v?"]);
        }
        else
        {
            foreach (var stream in mediaInfo.VideoStreams.Skip(1))
                arguments.AddRange(["-map", $"-0:{stream.Index}"]);
        }

        if (EffectiveAudioMode(job) == AudioMode.Remove)
            arguments.AddRange(["-map", "-0:a?"]);
        if (job.StreamHandling.RemoveSubtitles)
            arguments.AddRange(["-map", "-0:s?"]);
        if (job.StreamHandling.RemoveData)
            arguments.AddRange(["-map", "-0:d?"]);

        return arguments;
    }

    private static List<string> BuildStreamArguments(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var arguments = new List<string>();
        var audioMode = EffectiveAudioMode(job);

        if (audioMode == AudioMode.Copy)
        {
            arguments.AddRange(["-c:a", "copy"]);
        }
        else if (audioMode == AudioMode.Reencode)
        {
            arguments.AddRange(BuildAudioReencodeArguments(job));
        }

        if (!job.StreamHandling.RemoveSubtitles && mediaInfo.SubtitleStreams.Length > 0)
            arguments.AddRange(["-c:s", "copy"]);
        if (!job.StreamHandling.RemoveData && mediaInfo.DataStreams.Length > 0)
            arguments.AddRange(["-c:d", "copy"]);

        return arguments;
    }

    private static List<string> BuildAudioReencodeArguments(JobDefinition job)
    {
        var codec = job.AudioOptions.Codec;
        var bitrate = job.AudioOptions.Bitrate;

        if (codec is null || bitrate is null || bitrate <= 0)
            throw new CommandBuildException("Audio re-encode mode requires a codec and positive bitrate.");

        var arguments = new List<string>
        {
            "-c:a", AudioEncoderName(codec.Value),
            "-b:a", bitrate.Value.ToString(),
        };

        if (job.AudioOptions.AudioChannels is { } channels)
            arguments.AddRange(["-ac", channels.ToString()]);

        if (job.AudioOptions.SampleRate is { } sampleRate)
            arguments.AddRange(["-ar", sampleRate.ToString()]);

        return arguments;
    }

    private static List<string> BuildVideoCodecArguments(
        JobDefinition job,
        SourceMediaInfo mediaInfo,
        int? bitrate = null,
        int? passIndex = null)
    {
        var arguments = new List<string> { "-c:v", VideoEncoderName(job.VideoCodec) };

        if (job.VideoCodec == VideoCodec.H264)
            arguments.AddRange(["-pix_fmt", "yuv420p"]);

        var filterChain = BuildVideoFilterChain(job, mediaInfo);
        if (filterChain is not null)
            arguments.AddRange(["-vf", filterChain]);

        if (job.FrameRatePreset != FrameRatePreset.Original)
            arguments.AddRange(["-r", FrameRateValue(job.FrameRatePreset).ToString()]);

        if (job.EncodingStrategy == EncodingStrategy.TargetQuality)
        {
            arguments.AddRange(QualityArguments(job, passIndex));
            return arguments;
        }

        arguments.AddRange(VideoEncoderTuningArguments(job, passIndex));
        var resolvedBitrate = bitrate ?? ResolveVideoBitrate(job, mediaInfo);
        arguments.AddRange(["-b:v", FormatVideoBitrateArgument(resolvedBitrate)]);

        return arguments;
    }

    private static List<string> QualityArguments(JobDefinition job, int? passIndex)
    {
        var quality = job.TargetQuality
            ?? throw new CommandBuildException("Quality encoding requires codec-specific quality settings.");

        return quality switch
        {
            H264QualityOptions h264 =>
                ["-crf", h264.Crf.ToString(), "-preset", h264.Preset, "-profile:v", h264.Profile],

            H265QualityOptions h265 =>
                ["-crf", h265.Crf.ToString(), "-preset", h265.Preset, "-profile:v", h265.Profile],

            Vp8QualityOptions vp8 =>
            [
                "-crf", vp8.Quality.ToString(), "-b:v", "0",
                .. VpxEncoderTuningArguments(job,
                    ResolveVpxEncoderOptions(job.VideoCodec, job.VideoEncoderOptions), passIndex),
            ],

            Vp9QualityOptions vp9 =>
            [
                "-crf", vp9.Quality.ToString(), "-b:v", "0",
                .. VpxEncoderTuningArguments(job,
                    ResolveVpxEncoderOptions(job.VideoCodec, job.VideoEncoderOptions), passIndex),
            ],

            Av1QualityOptions av1 =>
                ["-crf", av1.Crf.ToString(), "-preset", av1.Preset.ToString()],

            _ => throw new CommandBuildException("Unsupported quality settings were provided."),
        };
    }

    private static List<string> VideoEncoderTuningArguments(JobDefinition job, int? passIndex)
    {
        if (job.VideoCodec == VideoCodec.H264)
        {
            var encoder = job.VideoEncoderOptions as H264EncoderOptions ?? new H264EncoderOptions();
            if (job.VideoEncoderOptions is not null and not H264EncoderOptions)
                throw new CommandBuildException("H.264 bitrate-based encoding requires preset and profile.");
            return ["-preset", encoder.Preset, "-profile:v", encoder.Profile];
        }

        if (job.VideoCodec == VideoCodec.H265)
        {
            var encoder = job.VideoEncoderOptions as H265EncoderOptions ?? new H265EncoderOptions();
            if (job.VideoEncoderOptions is not null and not H265EncoderOptions)
                throw new CommandBuildException("H.265 bitrate-based encoding requires preset and profile.");
            return ["-preset", encoder.Preset, "-profile:v", encoder.Profile];
        }

        if (job.VideoCodec == VideoCodec.Av1)
        {
            var encoder = job.VideoEncoderOptions as Av1EncoderOptions ?? new Av1EncoderOptions();
            if (job.VideoEncoderOptions is not null and not Av1EncoderOptions)
                throw new CommandBuildException("AV1 bitrate-based encoding requires a valid preset.");
            return ["-preset", encoder.Preset.ToString()];
        }

        return VpxEncoderTuningArguments(job,
            ResolveVpxEncoderOptions(job.VideoCodec, job.VideoEncoderOptions), passIndex);
    }

    private static VpxEncoderOptions ResolveVpxEncoderOptions(
        VideoCodec videoCodec,
        VideoEncoderOptions? configuredOptions)
    {
        if (configuredOptions is VpxEncoderOptions vpx)
        {
            if (vpx.Codec == videoCodec)
                return vpx;
            throw new CommandBuildException("VP8/VP9 encoding requires matching VPx encoder options.");
        }

        if (configuredOptions is not null)
            throw new CommandBuildException("VP8/VP9 encoding requires VPx encoder options.");

        return new VpxEncoderOptions(
            Codec: videoCodec,
            CpuUsed: EncoderDefaults.DefaultVpxCpuUsed(videoCodec));
    }

    private static List<string> VpxEncoderTuningArguments(
        JobDefinition job,
        VpxEncoderOptions encoder,
        int? passIndex)
    {
        var cpuUsed = encoder.CpuUsed;
        if (UsesTwoPass(job) && passIndex == 1)
            cpuUsed = Math.Max(cpuUsed, 4);

        var arguments = new List<string> { "-deadline", "good", "-cpu-used", cpuUsed.ToString() };

        if (encoder.TileColumns > 0)
            arguments.AddRange(["-tile-columns", encoder.TileColumns.ToString()]);
        if (encoder.TileRows > 0)
            arguments.AddRange(["-tile-rows", encoder.TileRows.ToString()]);

        if (job.VideoCodec == VideoCodec.Vp9)
            arguments.AddRange(["-row-mt", "1"]);
        if (UsesTwoPass(job) && passIndex == 2)
            arguments.AddRange(["-auto-alt-ref", "1"]);

        return arguments;
    }

    private static int ResolveVideoBitrate(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        if (job.EncodingStrategy == EncodingStrategy.TargetBitrate)
        {
            if (job.TargetBitrate is null || job.TargetBitrate.VideoBitrate <= 0)
                throw new CommandBuildException("Target bitrate mode requires a positive video bitrate.");
            return job.TargetBitrate.VideoBitrate;
        }

        if (job.EncodingStrategy == EncodingStrategy.TargetFileSize)
        {
            if (job.TargetFileSize is null)
                throw new CommandBuildException("Target file size mode requires file size options.");
            if (mediaInfo.DurationSeconds is null || mediaInfo.DurationSeconds <= 0)
                throw new CommandBuildException("Target file size mode requires a positive source duration.");

            var estimatedAudioBits = EstimateAudioBits(job, mediaInfo);
            var totalBudgetBits = (long)job.TargetFileSize.TargetSizeMb * 1024 * 1024 * 8;
            var usableBudgetBits = totalBudgetBits - job.TargetFileSize.EffectiveOverheadBits(totalBudgetBits);
            var videoBudgetBits = usableBudgetBits - estimatedAudioBits;

            if (videoBudgetBits <= 0)
                throw new CommandBuildException("The target file size leaves no bitrate budget for video.");

            return (int)(videoBudgetBits / mediaInfo.DurationSeconds.Value);
        }

        throw new CommandBuildException("Bitrate resolution was requested for a quality-based encode.");
    }

    private static int EstimateAudioBits(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var result = CompatibilityData.EstimateAudioBits(
            EffectiveAudioMode(job),
            job.AudioOptions.Bitrate,
            mediaInfo.AudioStreams,
            mediaInfo.DurationSeconds);

        return result
            ?? throw new CommandBuildException(
                "Audio size estimation failed: missing duration, bitrate, or stream information.");
    }

    private static string? BuildVideoFilterChain(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var filters = new List<string>();

        if (job.Crop is { } crop)
        {
            var sourceWidth = mediaInfo.PrimaryVideoStream?.Width ?? 0;
            var sourceHeight = mediaInfo.PrimaryVideoStream?.Height ?? 0;
            var isFullFrame = crop.X == 0 && crop.Y == 0
                              && crop.Width == sourceWidth && crop.Height == sourceHeight;
            if (!isFullFrame)
                filters.Add($"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}");
        }

        var scaleFilter = VideoScaleFilter(job, mediaInfo);
        if (scaleFilter is not null)
            filters.Add(scaleFilter);

        return filters.Count > 0 ? string.Join(",", filters) : null;
    }

    private static string? VideoScaleFilter(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        if (job.ResolutionPreset == ResolutionPreset.Original)
            return "scale=trunc(iw/2)*2:trunc(ih/2)*2";

        var resolution = TargetBoundingBox(job, mediaInfo);
        return $"scale={resolution.Width}:{resolution.Height}:" +
               "force_original_aspect_ratio=decrease:force_divisible_by=2";
    }

    private static Resolution TargetBoundingBox(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        if (!CompatibilityData.PresetResolutions.TryGetValue(job.ResolutionPreset, out var resolution))
            throw new CommandBuildException("Unsupported resolution preset.");

        var primaryVideo = mediaInfo.PrimaryVideoStream;
        if (primaryVideo is { Width: not null, Height: not null }
            && primaryVideo.Height > primaryVideo.Width)
        {
            return new Resolution(Width: resolution.Height, Height: resolution.Width);
        }

        return resolution;
    }

    private static AudioMode EffectiveAudioMode(JobDefinition job) =>
        job.AudioOptions.Mode;

    private static bool UsesTwoPass(JobDefinition job) => job.EncodingStrategy switch
    {
        EncodingStrategy.TargetFileSize => job.TargetFileSize is { TwoPass: true },
        EncodingStrategy.TargetBitrate => job.TargetBitrate is { TwoPass: true },
        EncodingStrategy.TargetQuality => job.TargetQuality is Vp8QualityOptions { TwoPass: true }
                                       or Vp9QualityOptions { TwoPass: true },
        _ => false,
    };

    private static string PassLogPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        return Path.Combine(directory, $".{stem}-ffpass");
    }

    private static string VideoEncoderName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "libx264",
        VideoCodec.H265 => "libx265",
        VideoCodec.Vp8 => "libvpx",
        VideoCodec.Vp9 => "libvpx-vp9",
        VideoCodec.Av1 => "libsvtav1",
        _ => throw new CommandBuildException($"Unsupported video codec: {codec}"),
    };

    private static string AudioEncoderName(AudioCodec codec) => codec switch
    {
        AudioCodec.Aac => "aac",
        AudioCodec.Mp3 => "libmp3lame",
        AudioCodec.Vorbis => "libvorbis",
        AudioCodec.Opus => "libopus",
        _ => throw new CommandBuildException($"Unsupported audio codec: {codec}"),
    };

    private static string FormatVideoBitrateArgument(int bitRate)
    {
        if (bitRate <= 0)
            throw new CommandBuildException("Video bitrate must be positive.");
        return $"{(int)Math.Round(bitRate / 1_000.0)}k";
    }

    private static int FrameRateValue(FrameRatePreset preset) => preset switch
    {
        FrameRatePreset.Fps24 => 24,
        FrameRatePreset.Fps25 => 25,
        FrameRatePreset.Fps30 => 30,
        FrameRatePreset.Fps50 => 50,
        FrameRatePreset.Fps60 => 60,
        _ => throw new CommandBuildException($"Unsupported frame rate preset: {preset}"),
    };

    public static string ContainerExtension(OutputContainer container) => container switch
    {
        OutputContainer.Mp4 => ".mp4",
        OutputContainer.Mkv => ".mkv",
        OutputContainer.Webm => ".webm",
        OutputContainer.Ogg => ".ogg",
        OutputContainer.M4a => ".m4a",
        OutputContainer.Mka => ".mka",
        OutputContainer.OpusFile => ".opus",
        _ => throw new CommandBuildException($"Unsupported container: {container}"),
    };

    private static string FormatSeconds(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.ToString(@"hh\:mm\:ss\.fff");
    }
}
