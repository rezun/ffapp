using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;

using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;

namespace FFApp.Core;

public sealed class CommandBuildException(string message)
    : InvalidOperationException(message);

public sealed class DefaultCommandBuilder(ExecutablePathsConfig pathsConfig) : ICommandBuilder
{
    private const string EncoderSettingsMetadataKey = "ENCODER_SETTINGS";

    // FFmpeg master filters most of these automatically when re-encoding. Explicit
    // deletion is harmless there and keeps stable FFmpeg 9.x and older correct.
    private static readonly string[] AlwaysInvalidMetadataFamilies =
        ["major_brand", "minor_version", "compatible_brands"];

    private static readonly string[] ReencodedMetadataFamilies =
    [
        "encoded_by",
        "encoding_tool",
        "vendor_id",
        "BPS",
        "DURATION",
        "NUMBER_OF_BYTES",
        "NUMBER_OF_FRAMES",
        "_STATISTICS_TAGS",
        "_STATISTICS_WRITING_APP",
        "_STATISTICS_WRITING_DATE_UTC",
        EncoderSettingsMetadataKey,
    ];

    private static readonly string[] ReencodedAudioMetadataFamilies =
        ["iTunPGAP", "iTunSMPB", "gapless_playback", "iTunNORM"];

    private static readonly string NullOutputPath =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "NUL" : "/dev/null";

    internal const string TwoPassLogSuffix = "ffpass";

    private static readonly string TrueHdCodecName = "truehd";

    public IReadOnlyList<GeneratedCommand> Build(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        // Matroska stores pictures as file attachments. Mapping them as video would
        // turn them into regular video tracks and lose attached_pic on the next probe.
        if (job.Container is not (OutputContainer.Mkv or OutputContainer.Mka)
            || job.StreamHandling.RemoveImages || mediaInfo.AttachedPictureStreams.Length == 0)
        {
            return BuildEncodingCommands(job, mediaInfo, []);
        }

        var attachments = mediaInfo.AttachedPictureStreams.Select(stream => new PictureAttachment(
            stream, Path.Combine(Path.GetTempPath(), $"ffapp-image-{Guid.NewGuid():N}"))).ToArray();
        var extractionArgs = new List<string> { "-hide_banner", "-y", "-i", job.SourcePath };
        foreach (var attachment in attachments)
        {
            extractionArgs.AddRange(["-map", $"0:{attachment.Stream.Index}", "-c:v", "copy",
                "-frames:v", "1", "-f", "image2", "-update", "1", attachment.Path]);
        }

        var extraction = CreateGeneratedCommand(pathsConfig.FfmpegPath, extractionArgs, ProcessProtocol.Plain) with
        {
            TemporaryFiles = [.. attachments.Select(attachment => attachment.Path)],
        };
        return [extraction, .. BuildEncodingCommands(job, mediaInfo, attachments)];
    }

    private sealed record PictureAttachment(AttachedPictureStreamInfo Stream, string Path);

    private IReadOnlyList<GeneratedCommand> BuildEncodingCommands(
        JobDefinition job, SourceMediaInfo mediaInfo, IReadOnlyList<PictureAttachment> attachments)
    {
        var inputArgs = new List<string> { "-hide_banner", "-y" };
        var trimStart = job.TrimStartSeconds ?? 0;
        if (trimStart > 0)
            inputArgs.AddRange(["-ss", FormatSeconds(trimStart)]);
        inputArgs.AddRange(BuildAudioDecoderArguments(
            job, AudioTrackSelection.Apply(mediaInfo, job.SelectedAudioStreamIndices)));
        inputArgs.AddRange(["-i", job.SourcePath]);
        if (job.TrimEndSeconds is > 0 && job.TrimEndSeconds < (mediaInfo.DurationSeconds ?? double.MaxValue))
        {
            // -to is relative to the output start (after -ss), so use duration, not absolute time
            var duration = job.TrimEndSeconds.Value - trimStart;
            if (duration > 0)
                inputArgs.AddRange(["-t", FormatSeconds(duration)]);
        }
        var mappingArgs = BuildMappingArguments(job, mediaInfo);
        var firstPassMappingArgs = UsesTwoPass(job)
            ? BuildMappingArguments(job, mediaInfo, includePictures: false) : mappingArgs;
        mediaInfo = TrackSelection.Apply(mediaInfo, job.SelectedAudioStreamIndices, job.SelectedSubtitleStreamIndices);
        var streamArgs = BuildStreamArguments(job, mediaInfo);
        var attachmentIndex = StreamCompatibilityPolicy.ShouldCopyDataStreams(job.Container, job.StreamHandling.RemoveData)
            ? mediaInfo.DataStreams.Count(stream => stream.IsAttachment) : 0;
        foreach (var attachment in attachments)
        {
            var mimeType = attachment.Stream.MimeType ?? attachment.Stream.CodecName switch
            {
                "mjpeg" => "image/jpeg",
                "png" => "image/png",
                "webp" => "image/webp",
                "gif" => "image/gif",
                "bmp" => "image/bmp",
                _ => "application/octet-stream",
            };
            var fileName = attachment.Stream.FileName ?? $"image-{attachment.Stream.Index}";
            streamArgs.AddRange(["-attach", attachment.Path,
                $"-metadata:s:t:{attachmentIndex}", $"mimetype={mimeType}",
                $"-metadata:s:t:{attachmentIndex}", $"filename={fileName}"]);
            attachmentIndex++;
        }
        var metadataCleanupArgs = BuildMetadataCleanupArguments(job, mediaInfo);

        // Audio-only: no video encoding, no two-pass
        if (job.VideoMode == VideoMode.Remove)
        {
            var arguments = new List<string>(inputArgs);
            arguments.AddRange(mappingArgs);
            arguments.AddRange(streamArgs);
            arguments.AddRange(metadataCleanupArgs);
            arguments.AddRange(BuildEncodingSettingsMetadataArguments(job, mediaInfo));
            if (CopiedPictureStreams(job, mediaInfo).Count > 0)
                arguments.AddRange(BuildPictureCodecArguments(job, mediaInfo));
            else
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
            arguments.AddRange(metadataCleanupArgs);
            arguments.AddRange(BuildEncodingSettingsMetadataArguments(job, mediaInfo));
            arguments.AddRange(["-c:v", "copy"]);
            arguments.AddRange(BuildPictureCodecArguments(job, mediaInfo));
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
            firstPassArgs.AddRange(firstPassMappingArgs);
            firstPassArgs.AddRange(BuildVideoCodecArguments(job, mediaInfo, bitrate: bitrate, passIndex: 1));
            firstPassArgs.AddRange(["-pass", "1", "-passlogfile", passLogPath, "-an", "-sn", "-dn", "-f", "null", NullOutputPath]);

            var secondPassArgs = new List<string>(inputArgs);
            secondPassArgs.AddRange(mappingArgs);
            secondPassArgs.AddRange(streamArgs);
            secondPassArgs.AddRange(metadataCleanupArgs);
            var secondPassVideoArgs = BuildVideoCodecArguments(job, mediaInfo, bitrate: bitrate, passIndex: 2);
            secondPassArgs.AddRange(secondPassVideoArgs);
            secondPassArgs.AddRange(BuildEncodingSettingsMetadataArguments(
                job, mediaInfo, secondPassVideoArgs, isTwoPass: true));
            secondPassArgs.AddRange(["-pass", "2", "-passlogfile", passLogPath]);
            secondPassArgs.AddRange(BuildPictureCodecArguments(job, mediaInfo));
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
        reencodeArgs.AddRange(metadataCleanupArgs);
        var videoCodecArgs = BuildVideoCodecArguments(job, mediaInfo);
        reencodeArgs.AddRange(videoCodecArgs);
        reencodeArgs.AddRange(BuildEncodingSettingsMetadataArguments(job, mediaInfo, videoCodecArgs));
        reencodeArgs.AddRange(BuildPictureCodecArguments(job, mediaInfo));
        if (job.Container == OutputContainer.Mp4)
            reencodeArgs.AddRange(["-movflags", "+faststart"]);
        reencodeArgs.Add(job.OutputPath);

        return [CreateGeneratedCommand(pathsConfig.FfmpegPath, reencodeArgs)];
    }

    public static string SuggestOutputPath(
        string sourcePath, OutputContainer container,
        string? outputDirectory = null, string? outputFilename = null,
        bool isAutoName = false,
        IReadOnlyCollection<string>? reservedOutputPaths = null)
    {
        var extension = ContainerExtension(container);
        var directory = outputDirectory ?? Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var stem = outputFilename ?? Path.GetFileNameWithoutExtension(sourcePath);
        var candidate = Path.Combine(directory, stem + extension);

        // When the user explicitly chose a filename, use it as-is (ffmpeg -y handles overwrites)
        if (!isAutoName)
            return candidate;

        var reservedPaths = reservedOutputPaths is null
            ? null
            : new HashSet<string>(reservedOutputPaths.Select(Path.GetFullPath), OutputNaming.PathComparer);

        if (IsOutputPathAvailable(candidate, reservedPaths))
            return candidate;

        for (var counter = 1; counter <= 10_000; counter++)
        {
            var numbered = Path.Combine(directory, $"{stem}-{counter}{extension}");
            if (IsOutputPathAvailable(numbered, reservedPaths))
                return numbered;
        }

        throw new CommandBuildException(
            "Could not find an available output filename after 10,000 attempts.");
    }

    private static bool IsOutputPathAvailable(string path, IReadOnlySet<string>? reservedPaths) =>
        !File.Exists(path)
        && (reservedPaths is null || !reservedPaths.Contains(Path.GetFullPath(path)));

    // --- Private helpers ---

    internal static GeneratedCommand CreateGeneratedCommand(
        string executable,
        IReadOnlyList<string> arguments,
        ProcessProtocol protocol = ProcessProtocol.Ffmpeg) =>
        GeneratedCommand.Create(executable, arguments, protocol);

    private static List<string> BuildMappingArguments(JobDefinition job, SourceMediaInfo mediaInfo, bool includePictures = true)
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

        // Append pictures after the primary video so v:0 always refers to the movie.
        if (job.VideoMode != VideoMode.Remove)
            foreach (var stream in mediaInfo.AttachedPictureStreams)
                arguments.AddRange(["-map", $"-0:{stream.Index}"]);
        if (includePictures)
            foreach (var stream in CopiedPictureStreams(job, mediaInfo))
                arguments.AddRange(["-map", $"0:{stream.Index}"]);

        if (job.AudioOptions.Mode == AudioMode.Remove)
            arguments.AddRange(["-map", "-0:a?"]);
        else if (job.SelectedAudioStreamIndices is { } selected)
        {
            if (selected.Any(index => !mediaInfo.AudioStreams.Any(stream => stream.Index == index)))
                throw new CommandBuildException("Selected audio track does not exist in the source.");
            foreach (var stream in mediaInfo.AudioStreams.Where(stream => !selected.Contains(stream.Index)))
                arguments.AddRange(["-map", $"-0:{stream.Index}"]);
        }
        var subtitlePolicy = StreamCompatibilityPolicy.ResolveSubtitlePolicy(
            job.Container,
            mediaInfo.SubtitleStreams,
            job.StreamHandling.RemoveSubtitles);
        if (subtitlePolicy.Mode is SubtitleStreamMode.Remove)
            arguments.AddRange(["-map", "-0:s?"]);
        else if (job.SelectedSubtitleStreamIndices is { } selectedSubtitles)
        {
            if (selectedSubtitles.Any(index => !mediaInfo.SubtitleStreams.Any(stream => stream.Index == index)))
                throw new CommandBuildException("Selected subtitle track does not exist in the source.");
            foreach (var stream in mediaInfo.SubtitleStreams.Where(stream => !selectedSubtitles.Contains(stream.Index)))
                arguments.AddRange(["-map", $"-0:{stream.Index}"]);
        }

        if (mediaInfo.DataStreams.Length > 0
            && !StreamCompatibilityPolicy.ShouldCopyDataStreams(job.Container, job.StreamHandling.RemoveData))
        {
            arguments.AddRange(["-map", "-0:d?"]);
            if (mediaInfo.DataStreams.Any(stream => stream.IsAttachment))
                arguments.AddRange(["-map", "-0:t?"]);
        }

        return arguments;
    }

    private static IReadOnlyList<AttachedPictureStreamInfo> CopiedPictureStreams(JobDefinition job, SourceMediaInfo mediaInfo) =>
        job.StreamHandling.RemoveImages || job.Container is OutputContainer.Mkv or OutputContainer.Mka
            ? []
            : mediaInfo.AttachedPictureStreams.Where(stream =>
                StreamCompatibilityPolicy.CanCopyAttachedPicture(job.Container, stream)).ToArray();

    private static IReadOnlyList<string> BuildPictureCodecArguments(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var arguments = new List<string>();
        var index = job.VideoMode == VideoMode.Remove || mediaInfo.PrimaryVideoStream is null ? 0 : 1;
        foreach (var stream in CopiedPictureStreams(job, mediaInfo))
        {
            arguments.AddRange([$"-c:v:{index}", "copy", $"-disposition:v:{index}", "attached_pic"]);
            index++;
        }
        return arguments;
    }

    private static List<string> BuildStreamArguments(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var arguments = new List<string>();
        var audioMode = job.AudioOptions.Mode;

        if (audioMode == AudioMode.Copy)
        {
            arguments.AddRange(["-c:a", "copy"]);
        }
        else if (audioMode == AudioMode.Reencode)
        {
            arguments.AddRange(BuildAudioReencodeArguments(job));
            if (job.AudioOptions.Codec == AudioCodec.Opus
                && job.AudioOptions.AudioChannels is null or 6)
            {
                for (var index = 0; index < mediaInfo.AudioStreams.Length; index++)
                {
                    if (string.Equals(mediaInfo.AudioStreams[index].ChannelLayout, "5.1(side)", StringComparison.Ordinal))
                    {
                        // libopus requires the Vorbis 5.1 layout. Preserve each channel's
                        // samples, assigning side surrounds to the corresponding back surrounds.
                        arguments.AddRange([$"-filter:a:{index}", "pan=5.1|FL=FL|FR=FR|FC=FC|LFE=LFE|BL=SL|BR=SR"]);
                    }
                }
            }
        }

        var subtitlePolicy = StreamCompatibilityPolicy.ResolveSubtitlePolicy(
            job.Container,
            mediaInfo.SubtitleStreams,
            job.StreamHandling.RemoveSubtitles);
        switch (subtitlePolicy.Mode)
        {
            case SubtitleStreamMode.Copy:
                arguments.AddRange(["-c:s", "copy"]);
                break;
            case SubtitleStreamMode.Convert:
                arguments.AddRange(["-c:s", subtitlePolicy.EncoderName
                    ?? throw new CommandBuildException("Subtitle conversion requires an encoder.")]);
                break;
            case SubtitleStreamMode.Unsupported:
                throw new CommandBuildException(
                    $"Subtitle stream {subtitlePolicy.UnsupportedStream?.Index ?? 0} cannot be preserved "
                        + $"in {job.Container.ToString().ToUpperInvariant()} output.");
        }

        if (mediaInfo.DataStreams.Length > 0
            && StreamCompatibilityPolicy.ShouldCopyDataStreams(job.Container, job.StreamHandling.RemoveData))
        {
            arguments.AddRange(["-c:d", "copy"]);
        }

        return arguments;
    }

    private static List<string> BuildMetadataCleanupArguments(
        JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var arguments = new List<string>();
        var videoReencoded = job.VideoMode == VideoMode.Reencode;
        var audioReencoded = job.AudioOptions.Mode == AudioMode.Reencode;

        AddMetadataDeletions(
            arguments,
            metadataSpecifier: null,
            mediaInfo.MetadataKeys,
            reencoded: videoReencoded || audioReencoded,
            audioReencoded);

        if (job.VideoMode != VideoMode.Remove && mediaInfo.PrimaryVideoStream is { } video)
        {
            AddMetadataDeletions(
                arguments,
                "s:v:0",
                video.MetadataKeys,
                videoReencoded,
                audioReencoded: false);
        }

        if (job.AudioOptions.Mode != AudioMode.Remove)
        {
            if (job.AudioOptions.RemoveTrackTitles && mediaInfo.AudioStreams.Length > 0)
                arguments.AddRange(["-metadata:s:a", "title="]);

            for (var index = 0; index < mediaInfo.AudioStreams.Length; index++)
            {
                AddMetadataDeletions(
                    arguments,
                    $"s:a:{index}",
                    mediaInfo.AudioStreams[index].MetadataKeys,
                    audioReencoded,
                    audioReencoded);
            }
        }

        return arguments;
    }

    private static void AddMetadataDeletions(
        List<string> arguments,
        string? metadataSpecifier,
        ImmutableArray<string> metadataKeys,
        bool reencoded,
        bool audioReencoded)
    {
        foreach (var key in metadataKeys)
        {
            if (!MatchesAnyMetadataFamily(key, AlwaysInvalidMetadataFamilies)
                && !(reencoded && MatchesAnyMetadataFamily(key, ReencodedMetadataFamilies))
                && !(audioReencoded && MatchesAnyMetadataFamily(key, ReencodedAudioMetadataFamilies)))
            {
                continue;
            }

            arguments.Add(metadataSpecifier is null ? "-metadata" : $"-metadata:{metadataSpecifier}");
            arguments.Add($"{key}=");
        }
    }

    private static bool MatchesAnyMetadataFamily(string key, IReadOnlyList<string> families)
    {
        foreach (var family in families)
        {
            if (key.Equals(family, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(family + "-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> BuildEncodingSettingsMetadataArguments(
        JobDefinition job,
        SourceMediaInfo mediaInfo,
        IReadOnlyList<string>? videoCodecArguments = null,
        bool isTwoPass = false)
    {
        if (!SupportsEncodingSettingsMetadata(job.Container))
            return [];

        var arguments = new List<string>();

        if (job.VideoMode == VideoMode.Reencode
            && mediaInfo.PrimaryVideoStream is not null
            && videoCodecArguments is not null)
        {
            var settings = FormatEncodingSettings(videoCodecArguments);
            if (isTwoPass)
                settings += "; passes=2";
            arguments.AddRange(["-metadata:s:v:0", $"{EncoderSettingsMetadataKey}={settings}"]);
        }

        if (job.AudioOptions.Mode == AudioMode.Reencode && mediaInfo.AudioStreams.Length > 0)
        {
            var settings = FormatEncodingSettings(BuildAudioReencodeArguments(job));
            arguments.AddRange(["-metadata:s:a", $"{EncoderSettingsMetadataKey}={settings}"]);
        }

        return arguments;
    }

    private static bool SupportsEncodingSettingsMetadata(OutputContainer container) => container switch
    {
        OutputContainer.Mkv or OutputContainer.Mka or OutputContainer.Webm
            or OutputContainer.Ogg or OutputContainer.OpusFile => true,
        OutputContainer.Mp4 or OutputContainer.M4a => false,
        _ => throw new CommandBuildException($"Unsupported container: {container}"),
    };

    private static string FormatEncodingSettings(IReadOnlyList<string> codecArguments)
    {
        var settings = new List<string>(codecArguments.Count / 2);
        for (var index = 0; index < codecArguments.Count; index += 2)
        {
            if (index + 1 >= codecArguments.Count)
                throw new CommandBuildException("Encoder argument is missing its value.");

            settings.Add($"{codecArguments[index].TrimStart('-')}={codecArguments[index + 1]}");
        }

        return string.Join("; ", settings);
    }

    // TrueHD embeds encoder-made stereo and 5.1 presentations. Decoding one of them avoids
    // a generic swresample downmix; a stream without it decodes in full and -ac still downmixes.
    private static List<string> BuildAudioDecoderArguments(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var arguments = new List<string>();
        if (job.AudioOptions.Mode != AudioMode.Reencode
            || job.AudioOptions.AudioChannels is not { } channels
            || TrueHdDownmixLayout(channels) is not { } layout)
        {
            return arguments;
        }

        foreach (var stream in mediaInfo.AudioStreams.Where(stream =>
            string.Equals(stream.CodecName, TrueHdCodecName, StringComparison.Ordinal) && stream.Channels > channels))
        {
            arguments.AddRange([$"-downmix:{stream.Index}", layout]);
        }
        return arguments;
    }

    // The decoder accepts only these layouts; FFmpeg's "5.1" is the back-surround variant and is rejected.
    private static string? TrueHdDownmixLayout(int channels) => channels switch
    {
        2 => "stereo",
        6 => "5.1(side)",
        _ => null,
    };

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
        {
            if (!CompatibilityData.IsAudioSampleRateSupported(codec.Value, sampleRate))
                throw new CommandBuildException($"Sample rate {sampleRate} Hz is not supported by {codec.Value}.");
            arguments.AddRange(["-ar", sampleRate.ToString(CultureInfo.InvariantCulture)]);
        }

        return arguments;
    }

    private static List<string> BuildVideoCodecArguments(
        JobDefinition job,
        SourceMediaInfo mediaInfo,
        int? bitrate = null,
        int? passIndex = null)
    {
        var arguments = new List<string> { "-c:v", VideoEncoderName(job.VideoCodec) };

        if (job is { VideoCodec: VideoCodec.H265, Container: OutputContainer.Mp4 })
            arguments.AddRange(["-tag:v", "hvc1"]);

        arguments.AddRange(PixelFormatArguments(job, mediaInfo));
        arguments.AddRange(ColorMetadataArguments(job, mediaInfo));

        var filterChain = BuildVideoFilterChain(job, mediaInfo);
        if (filterChain is not null)
            arguments.AddRange(["-vf", filterChain]);

        if (job.FrameRatePreset != FrameRatePreset.Original)
            arguments.AddRange(["-r", FrameRateValue(job.FrameRatePreset)]);

        if (job.EncodingStrategy == EncodingStrategy.TargetQuality)
        {
            arguments.AddRange(QualityArguments(job, mediaInfo, passIndex));
        }
        else
        {
            arguments.AddRange(VideoEncoderTuningArguments(job, mediaInfo, passIndex));
            var resolvedBitrate = bitrate ?? ResolveVideoBitrate(job, mediaInfo);
            arguments.AddRange(["-b:v", FormatVideoBitrateArgument(resolvedBitrate)]);
        }

        if (job.KeyframeInterval is > 0
            && !(job.VideoCodec == VideoCodec.Av1 && job.KeyframeUnit == KeyframeUnit.Seconds))
        {
            arguments.AddRange(["-g", job.KeyframeInterval.Value.ToString()]);
        }

        if (job.VideoCodec == VideoCodec.Av1)
            arguments.AddRange(BuildSvtAv1Params(job, mediaInfo));

        arguments.AddRange(CodecParamsArguments(job, mediaInfo));

        // Filters cannot apply to stream-copied cover art. Keep video transforms
        // scoped to the primary video while the image streams remain untouched.
        if (CopiedPictureStreams(job, mediaInfo).Count > 0)
        {
            for (var i = 0; i < arguments.Count; i += 2)
            {
                arguments[i] = arguments[i] switch
                {
                    "-vf" => "-filter:v:0",
                    "-pix_fmt" => "-pix_fmt:v:0",
                    "-r" => "-r:v:0",
                    var option when option.EndsWith(":v", StringComparison.Ordinal) => option + ":0",
                    var option => option,
                };
            }
        }

        return arguments;
    }

    private static List<string> CodecParamsArguments(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var additional = ExtractAdditionalOptions(job.VideoEncoderOptions);

        return job.VideoCodec switch
        {
            VideoCodec.H264 => string.IsNullOrWhiteSpace(additional)
                ? []
                : ["-x264-params", additional.Trim()],
            VideoCodec.H265 => BuildX265ParamsArguments(job, mediaInfo, additional),
            VideoCodec.Vp9 => BuildVpxExtraFlags(additional),
            // AV1 additional options are merged inside BuildSvtAv1Params
            _ => [],
        };
    }

    private static string ExtractAdditionalOptions(VideoEncoderOptions? options) => options switch
    {
        H26xEncoderOptions h26x => h26x.AdditionalOptions,
        VpxEncoderOptions vpx => vpx.AdditionalOptions,
        Av1EncoderOptions av1 => av1.AdditionalOptions,
        _ => "",
    };

    private static List<string> BuildX265ParamsArguments(
        JobDefinition job, SourceMediaInfo mediaInfo, string additional)
    {
        var parts = new List<string>();

        var video = mediaInfo.PrimaryVideoStream;
        if (video is not null && video.IsHdr && job.HdrMode == HdrMode.Preserve)
            parts.AddRange(BuildX265HdrParts(video));

        if (!string.IsNullOrWhiteSpace(additional))
            parts.Add(additional.Trim());

        return parts.Count == 0 ? [] : ["-x265-params", string.Join(":", parts)];
    }

    private static List<string> BuildVpxExtraFlags(string additional)
    {
        if (string.IsNullOrWhiteSpace(additional))
            return [];

        var flags = new List<string>();
        foreach (var segment in additional.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = segment[..eq].Trim();
            var value = segment[(eq + 1)..].Trim();
            if (key.Length == 0)
                continue;
            flags.Add("-" + key);
            flags.Add(value);
        }
        return flags;
    }

    private static List<string> QualityArguments(JobDefinition job, SourceMediaInfo mediaInfo, int? passIndex)
    {
        var quality = job.TargetQuality
            ?? throw new CommandBuildException("Quality encoding requires codec-specific quality settings.");

        return quality switch
        {
            H26xQualityOptions h26x when h26x.Codec == VideoCodec.H264 =>
                ["-crf", h26x.Crf.ToString(), "-preset", h26x.Preset, "-profile:v", h26x.Profile],

            H26xQualityOptions h26x when h26x.Codec == VideoCodec.H265 =>
                ["-crf", h26x.Crf.ToString(), "-preset", h26x.Preset,
                 "-profile:v", ResolveH265Profile(job, mediaInfo, h26x.Profile)],

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

    private static List<string> VideoEncoderTuningArguments(JobDefinition job, SourceMediaInfo mediaInfo, int? passIndex)
    {
        if (job.VideoCodec is VideoCodec.H264 or VideoCodec.H265)
        {
            var encoder = job.VideoEncoderOptions switch
            {
                H26xEncoderOptions h26x when h26x.Codec == job.VideoCodec => h26x,
                null => H26xEncoderOptions.ForCodec(job.VideoCodec),
                _ => throw new CommandBuildException(
                    $"{CompatibilityData.VideoCodecLabel(job.VideoCodec)} bitrate-based encoding requires preset and profile."),
            };
            var profile = job.VideoCodec == VideoCodec.H265
                ? ResolveH265Profile(job, mediaInfo, encoder.Profile)
                : encoder.Profile;
            return ["-preset", encoder.Preset, "-profile:v", profile];
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
            throw new CommandBuildException("VP9 encoding requires matching VPx encoder options.");
        }

        if (configuredOptions is not null)
            throw new CommandBuildException("VP9 encoding requires VPx encoder options.");

        return new VpxEncoderOptions(Codec: videoCodec);
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

    private static double GetEffectiveDuration(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        if (mediaInfo.DurationSeconds is null || mediaInfo.DurationSeconds <= 0)
            throw new CommandBuildException("Target file size mode requires a positive source duration.");

        var start = job.TrimStartSeconds ?? 0;
        var end = job.TrimEndSeconds ?? mediaInfo.DurationSeconds.Value;
        var trimmed = end - start;
        return trimmed > 0 ? trimmed : mediaInfo.DurationSeconds.Value;
    }

    internal static int ResolveVideoBitrate(JobDefinition job, SourceMediaInfo mediaInfo)
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

            var duration = GetEffectiveDuration(job, mediaInfo);
            var estimatedAudioBits = EstimateAudioBits(job, mediaInfo, duration);
            var totalBudgetBits = (long)job.TargetFileSize.TargetSizeMb * 1024 * 1024 * 8;
            var usableBudgetBits = totalBudgetBits - job.TargetFileSize.EffectiveOverheadBits(totalBudgetBits);
            var videoBudgetBits = usableBudgetBits - estimatedAudioBits;

            if (videoBudgetBits <= 0)
                throw new CommandBuildException("The target file size leaves no bitrate budget for video.");

            return (int)Math.Min(videoBudgetBits / duration, int.MaxValue);
        }

        throw new CommandBuildException("Bitrate resolution was requested for a quality-based encode.");
    }

    private static long EstimateAudioBits(JobDefinition job, SourceMediaInfo mediaInfo, double duration)
    {
        var result = CompatibilityData.EstimateAudioBits(
            job.AudioOptions.Mode,
            job.AudioOptions.Bitrate,
            mediaInfo.AudioStreams,
            duration);

        return result
            ?? throw new CommandBuildException(
                "Audio size estimation failed: missing duration, bitrate, or stream information.");
    }

    private static string? BuildVideoFilterChain(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var filters = VideoFilters(job, mediaInfo);
        return filters.Count > 0 ? string.Join(",", filters) : null;
    }

    internal static IReadOnlyList<string> VideoFilters(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var filters = new List<string>();

        var isToneMapping = mediaInfo.PrimaryVideoStream?.IsHdr == true
            && job.HdrMode == HdrMode.TonemapToSdr;
        var colorFilter = ColorMetadataFrameFilter(job, mediaInfo);
        if (!isToneMapping && colorFilter is not null)
            filters.Add(colorFilter);

        filters.AddRange(ToneMapFilters(job, mediaInfo));

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

        if (isToneMapping && colorFilter is not null)
            filters.Add(colorFilter);

        return filters;
    }

    private static string? ColorMetadataFrameFilter(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var colors = ColorMetadataResolver.Resolve(mediaInfo.PrimaryVideoStream, job.ColorMetadata, job.HdrMode);
        var parts = new List<string>();
        if (colors.Primaries is { } primaries)
            parts.Add($"color_primaries={primaries}");
        if (colors.Transfer is { } transfer)
        {
            // setparams uses different aliases from the encoder color_trc option.
            var frameTransfer = transfer switch
            {
                "gamma22" => "bt470m", "gamma28" => "bt470bg",
                "log" => "log100", "log_sqrt" => "log316",
                "iec61966_2_4" => "iec61966-2-4", "iec61966_2_1" => "iec61966-2-1",
                "bt2020_10" => "bt2020-10", "bt2020_12" => "bt2020-12",
                _ => transfer,
            };
            parts.Add($"color_trc={frameTransfer}");
        }
        if (colors.Matrix is { } matrix)
            parts.Add($"colorspace={(matrix == "rgb" ? "gbr" : matrix)}");
        if (colors.Range is { } range)
            parts.Add($"range={range}");
        // Set frame properties as well as encoder options: some FFmpeg encoders
        // prefer frame metadata. Before scaling, this also prevents a manual
        // range correction from rescaling pixel levels.
        return parts.Count > 0 ? $"setparams={string.Join(":", parts)}" : null;
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
        if (primaryVideo is { DisplayWidth: not null, DisplayHeight: not null }
            && primaryVideo.DisplayHeight > primaryVideo.DisplayWidth)
        {
            return new Resolution(Width: resolution.Height, Height: resolution.Width);
        }

        return resolution;
    }

    private static bool UsesTwoPass(JobDefinition job) => job.EncodingStrategy switch
    {
        EncodingStrategy.TargetFileSize => job.TargetFileSize is { TwoPass: true },
        EncodingStrategy.TargetBitrate => job.TargetBitrate is { TwoPass: true },
        EncodingStrategy.TargetQuality => job.TargetQuality is Vp9QualityOptions { TwoPass: true },
        _ => false,
    };

    private static string PassLogPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        return Path.Combine(directory, $".{stem}-{TwoPassLogSuffix}");
    }

    internal static string VideoEncoderName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "libx264",
        VideoCodec.H265 => "libx265",
        VideoCodec.Vp9 => "libvpx-vp9",
        VideoCodec.Av1 => "libsvtav1",
        _ => throw new CommandBuildException($"Unsupported video codec: {codec}"),
    };

    internal static string AudioEncoderName(AudioCodec codec) => codec switch
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

    private static string FrameRateValue(FrameRatePreset preset) => preset switch
    {
        FrameRatePreset.Fps23_976 => "24000/1001",
        FrameRatePreset.Fps24 => "24",
        FrameRatePreset.Fps25 => "25",
        FrameRatePreset.Fps29_97 => "30000/1001",
        FrameRatePreset.Fps30 => "30",
        FrameRatePreset.Fps50 => "50",
        FrameRatePreset.Fps59_94 => "60000/1001",
        FrameRatePreset.Fps60 => "60",
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

    // --- HDR helpers ---

    private static List<string> PixelFormatArguments(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var isHdrPreserve = mediaInfo.PrimaryVideoStream?.IsHdr == true
                            && job.HdrMode == HdrMode.Preserve;

        return job.VideoCodec switch
        {
            VideoCodec.H264 => ["-pix_fmt", "yuv420p"],
            VideoCodec.H265 when isHdrPreserve => ["-pix_fmt", "yuv420p10le"],
            VideoCodec.Av1 => ["-pix_fmt", "yuv420p10le"],
            _ => [],
        };
    }

    private static List<string> ColorMetadataArguments(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var settings = job.ColorMetadata;
        if (!Enum.IsDefined(settings.Mode))
            throw new CommandBuildException("Unknown color metadata mode.");
        if (settings.Mode == ColorMetadataMode.Custom
            && !ColorMetadataResolver.HasHdrSignal(mediaInfo.PrimaryVideoStream)
            && (!CompatibilityData.ColorPrimariesOptions.Contains(settings.Primaries)
                || !CompatibilityData.ColorTransferOptions.Contains(settings.Transfer)
                || !CompatibilityData.ColorMatrixOptions.Contains(settings.Matrix)
                || !CompatibilityData.ColorRangeOptions.Contains(settings.Range)))
            throw new CommandBuildException("Invalid custom color metadata.");

        var colors = ColorMetadataResolver.Resolve(mediaInfo.PrimaryVideoStream, settings, job.HdrMode);
        var args = new List<string>();
        Add("-color_primaries", colors.Primaries);
        Add("-color_trc", colors.Transfer);
        Add("-colorspace", colors.Matrix);
        Add("-color_range", colors.Range);
        return args;

        void Add(string option, string? value)
        {
            if (value is not null)
                args.AddRange([option, value]);
        }
    }

    private static List<string> BuildSvtAv1Params(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var parts = new List<string>();

        // Keyframe interval in seconds (AV1-only feature)
        if (job.KeyframeInterval is > 0 && job.KeyframeUnit == KeyframeUnit.Seconds)
            parts.Add($"keyint={job.KeyframeInterval.Value}s");

        // HDR preservation
        var video = mediaInfo.PrimaryVideoStream;
        if (video is not null && video.IsHdr && job.HdrMode == HdrMode.Preserve)
            parts.Add("enable-hdr=1");

        // Resolve AV1-specific tuning from either quality or encoder options
        var (tune, enableOverlays, scm, enableVarianceBoost, varianceBoostStrength, varianceOctile) = job.TargetQuality switch
        {
            Av1QualityOptions av1 when job.EncodingStrategy == EncodingStrategy.TargetQuality
                => (av1.Tune, av1.EnableOverlays, av1.Scm, av1.EnableVarianceBoost, av1.VarianceBoostStrength, av1.VarianceOctile),
            _ => job.VideoEncoderOptions switch
            {
                Av1EncoderOptions enc => (enc.Tune, enc.EnableOverlays, enc.Scm, enc.EnableVarianceBoost, enc.VarianceBoostStrength, enc.VarianceOctile),
                _ => (0, 1, 2, 0, 2, 5),
            },
        };

        // Emit when value differs from SVT-AV1 encoder defaults
        // (tune=1, overlays=0, scm=2, variance-boost=0)
        if (tune != 1)
            parts.Add($"tune={tune}");
        if (enableOverlays != 0)
            parts.Add($"enable-overlays={enableOverlays}");
        if (scm != 2)
            parts.Add($"scm={scm}");
        if (enableVarianceBoost != 0)
        {
            parts.Add($"enable-variance-boost={enableVarianceBoost}");
            parts.Add($"variance-boost-strength={varianceBoostStrength}");
            parts.Add($"variance-octile={varianceOctile}");
        }

        var (filmGrain, filmGrainDenoise, adaptiveFilmGrain) = job.TargetQuality switch
        {
            Av1QualityOptions av1 when job.EncodingStrategy == EncodingStrategy.TargetQuality
                => (av1.FilmGrain, av1.FilmGrainDenoise, av1.AdaptiveFilmGrain),
            _ => job.VideoEncoderOptions switch
            {
                Av1EncoderOptions enc => (enc.FilmGrain, enc.FilmGrainDenoise, enc.AdaptiveFilmGrain),
                _ => (0, 0, 1),
            },
        };
        if (filmGrain > 0)
        {
            parts.Add($"film-grain={filmGrain}");
            // Set this explicitly: older SVT-AV1 versions enable denoising by default.
            parts.Add($"film-grain-denoise={filmGrainDenoise}");
            // Leave the default to the encoder for compatibility with older versions
            // that do not expose the adaptive-film-grain switch.
            if (adaptiveFilmGrain != 1)
                parts.Add($"adaptive-film-grain={adaptiveFilmGrain}");
        }

        // Capped CRF: max bit rate is only meaningful in TargetQuality mode.
        if (job.TargetQuality is Av1QualityOptions q
            && job.EncodingStrategy == EncodingStrategy.TargetQuality
            && q.MaxBitrateKbps > 0)
        {
            parts.Add($"mbr={q.MaxBitrateKbps}k");
        }

        if (job.VideoEncoderOptions is Av1EncoderOptions avEnc
            && !string.IsNullOrWhiteSpace(avEnc.AdditionalOptions))
        {
            parts.Add(avEnc.AdditionalOptions.Trim());
        }

        return parts.Count > 0 ? ["-svtav1-params", string.Join(":", parts)] : [];
    }

    private static List<string> BuildX265HdrParts(VideoStreamInfo video)
    {
        var colors = ColorMetadataResolver.Resolve(video, new ColorMetadataSettings(), HdrMode.Preserve);
        var parts = new List<string>
        {
            "hdr-opt=1",
            "repeat-headers=1",
            $"colorprim={colors.Primaries}",
            $"transfer={colors.Transfer}",
            $"colormatrix={colors.Matrix}",
        };

        if (video.HdrMetadata is { } metadata)
        {
            if (metadata.MaxContentLightLevel is not null
                && metadata.MaxAverageLightLevel is not null)
            {
                parts.Add($"max-cll={metadata.MaxContentLightLevel},{metadata.MaxAverageLightLevel}");
            }

            if (metadata.MasteringDisplay is { } md)
                parts.Add($"master-display={FormatMasteringDisplay(md)}");
        }

        return parts;
    }

    internal static string FormatMasteringDisplay(MasteringDisplayMetadata md) =>
        $"G({md.GreenX},{md.GreenY})B({md.BlueX},{md.BlueY})R({md.RedX},{md.RedY})" +
        $"WP({md.WhitePointX},{md.WhitePointY})L({md.MaxLuminance},{md.MinLuminance})";

    private static string ResolveH265Profile(
        JobDefinition job, SourceMediaInfo mediaInfo, string requestedProfile)
    {
        var isHdrPreserve = mediaInfo.PrimaryVideoStream?.IsHdr == true
                            && job.HdrMode == HdrMode.Preserve;
        return isHdrPreserve ? "main10" : requestedProfile;
    }

    private static List<string> ToneMapFilters(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var video = mediaInfo.PrimaryVideoStream;
        if (video is null || !video.IsHdr || job.HdrMode != HdrMode.TonemapToSdr)
            return [];

        var toneMap = job.ToneMapSettings;
        var outputFormat = job.VideoCodec == VideoCodec.Av1 ? "yuv420p10le" : "yuv420p";

        if (toneMap.Engine == ToneMapEngine.Libplacebo)
        {
            var parts = new List<string>
            {
                $"tonemapping={LibplaceboAlgorithmName(toneMap.LibplaceboAlgorithm)}",
                $"gamut_mode={GamutModeName(toneMap.GamutMode)}",
                string.Create(CultureInfo.InvariantCulture, $"saturation={toneMap.Saturation:F2}"),
                string.Create(CultureInfo.InvariantCulture, $"contrast_recovery={toneMap.ContrastRecovery:F2}"),
                $"peak_detect={(toneMap.PeakDetect ? 1 : 0)}",
                "color_primaries=bt709",
                "color_trc=bt709",
                "colorspace=bt709",
                "range=limited",
            };

            if (toneMap.TonemappingParam is > 0)
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"tonemapping_param={toneMap.TonemappingParam:F2}"));

            return
            [
                $"libplacebo={string.Join(":", parts)}",
                $"format={outputFormat}",
            ];
        }

        var tonemapParts = new List<string> { ToneMapAlgorithmName(toneMap.Algorithm) };

        if (toneMap.Desaturation != 2.0)
            tonemapParts.Add(string.Create(CultureInfo.InvariantCulture, $"desat={toneMap.Desaturation:F1}"));
        if (toneMap.PeakNits is { } peak)
            tonemapParts.Add($"peak={peak}");

        var tonemapFilter = $"tonemap={string.Join(":", tonemapParts)}";

        return
        [
            "zscale=t=linear",
            "format=gbrpf32le",
            "zscale=p=bt709",
            tonemapFilter,
            "zscale=t=bt709:m=bt709:r=tv",
            $"format={outputFormat}",
        ];
    }

    private static string ToneMapAlgorithmName(ToneMapAlgorithm algorithm) => algorithm switch
    {
        ToneMapAlgorithm.Hable => "hable",
        ToneMapAlgorithm.Reinhard => "reinhard",
        ToneMapAlgorithm.Mobius => "mobius",
        ToneMapAlgorithm.Bt2390 => "bt2390",
        ToneMapAlgorithm.Clip => "clip",
        _ => "hable",
    };

    private static string LibplaceboAlgorithmName(LibplaceboToneMapAlgorithm algorithm) => algorithm switch
    {
        LibplaceboToneMapAlgorithm.Auto => "auto",
        LibplaceboToneMapAlgorithm.Clip => "clip",
        LibplaceboToneMapAlgorithm.Bt2446a => "bt.2446a",
        LibplaceboToneMapAlgorithm.Bt2446c => "bt.2446c",
        LibplaceboToneMapAlgorithm.Spline => "spline",
        LibplaceboToneMapAlgorithm.Reinhard => "reinhard",
        LibplaceboToneMapAlgorithm.Hable => "hable",
        LibplaceboToneMapAlgorithm.Mobius => "mobius",
        LibplaceboToneMapAlgorithm.St2094_40 => "st2094-40",
        LibplaceboToneMapAlgorithm.St2094_10 => "st2094-10",
        _ => "auto",
    };

    private static string GamutModeName(GamutMode mode) => mode switch
    {
        GamutMode.Clip => "clip",
        GamutMode.Perceptual => "perceptual",
        GamutMode.Relative => "relative",
        GamutMode.Saturation => "saturation",
        GamutMode.Absolute => "absolute",
        GamutMode.Desaturate => "desaturate",
        GamutMode.Darken => "darken",
        GamutMode.Linear => "linear",
        _ => "perceptual",
    };

    private static string FormatSeconds(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.ToString(@"hh\:mm\:ss\.fff");
    }
}
