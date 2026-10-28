using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;

namespace FFApp.Infrastructure;

public sealed class MediaProbeException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class MediaProbeService(ExecutablePathsConfig pathsConfig) : IMediaProbeService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    public async Task<SourceMediaInfo> ProbeAsync(string sourcePath, CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath))
            throw new MediaProbeException($"Source file does not exist: {sourcePath}");

        string stdout;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pathsConfig.FfprobePath,
                    ArgumentList =
                    {
                        "-v", "error",
                        "-show_format",
                        "-show_streams",
                        "-show_chapters",
                        "-of", "json",
                        sourcePath,
                    },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProbeTimeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            stdout = await stdoutTask;
            var stderr = await stderrTask;
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new MediaProbeException(
                    $"ffprobe exited with code {process.ExitCode}: {detail.Trim()}");
            }
        }
        catch (MediaProbeException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new MediaProbeException("ffprobe timed out while probing the source file.");
        }
        catch (Exception ex)
        {
            throw new MediaProbeException($"Failed to run ffprobe: {ex.Message}", ex);
        }

        return ParseMediaInfo(sourcePath, stdout);
    }

    internal static SourceMediaInfo ParseMediaInfo(string sourcePath, string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new MediaProbeException($"Failed to parse ffprobe output: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;

            string? containerFormat = null;
            double? durationSeconds = null;
            int? bitRate = null;
            long? sizeBytes = null;
            string? title = null;
            ImmutableArray<string> metadataKeys = [];

            if (root.TryGetProperty("format", out var format))
            {
                containerFormat = GetString(format, "format_name");
                durationSeconds = GetDouble(format, "duration");
                bitRate = GetInt(format, "bit_rate");
                sizeBytes = GetLong(format, "size");
                title = GetTagIgnoreCase(format, "title");
                metadataKeys = ParseMetadataKeys(format);
            }

            var videoStreams = ImmutableArray.CreateBuilder<VideoStreamInfo>();
            var attachedPictureStreams = ImmutableArray.CreateBuilder<AttachedPictureStreamInfo>();
            var audioStreams = ImmutableArray.CreateBuilder<AudioStreamInfo>();
            var subtitleStreams = ImmutableArray.CreateBuilder<SubtitleStreamInfo>();
            var dataStreams = ImmutableArray.CreateBuilder<DataStreamInfo>();

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = GetString(stream, "codec_type");

                    switch (codecType)
                    {
                        case "video" when stream.TryGetProperty("disposition", out var disposition)
                            && GetInt(disposition, "attached_pic") == 1:
                            attachedPictureStreams.Add(new AttachedPictureStreamInfo(
                                Index: GetInt(stream, "index") ?? 0,
                                CodecName: GetString(stream, "codec_name"),
                                FileName: GetTag(stream, "filename"),
                                MimeType: GetTag(stream, "mimetype")));
                            break;
                        case "video":
                            videoStreams.Add(ParseVideoStream(stream));
                            break;
                        case "audio":
                            audioStreams.Add(ParseAudioStream(stream));
                            break;
                        case "subtitle":
                            subtitleStreams.Add(ParseSubtitleStream(stream));
                            break;
                        case "data" or "attachment":
                            dataStreams.Add(ParseDataStream(stream));
                            break;
                    }
                }
            }

            return new SourceMediaInfo
            {
                SourcePath = sourcePath,
                ContainerFormat = containerFormat,
                DurationSeconds = durationSeconds,
                BitRate = bitRate,
                SizeBytes = sizeBytes,
                Title = title,
                Chapters = ParseChapters(root),
                MetadataKeys = metadataKeys,
                VideoStreams = videoStreams.ToImmutable(),
                AttachedPictureStreams = attachedPictureStreams.ToImmutable(),
                AudioStreams = audioStreams.ToImmutable(),
                SubtitleStreams = subtitleStreams.ToImmutable(),
                DataStreams = dataStreams.ToImmutable(),
            };
        }
    }

    private static VideoStreamInfo ParseVideoStream(JsonElement stream)
    {
        return new VideoStreamInfo(
            Index: GetInt(stream, "index") ?? 0,
            CodecName: GetString(stream, "codec_name"),
            BitRate: GetInt(stream, "bit_rate"),
            Width: GetInt(stream, "width"),
            Height: GetInt(stream, "height"),
            FrameRate: ParseFrameRate(GetString(stream, "avg_frame_rate")),
            FrameCount: GetInt(stream, "nb_frames"),
            Rotation: ParseRotation(stream),
            PixelFormat: GetString(stream, "pix_fmt"),
            ColorPrimaries: GetString(stream, "color_primaries"),
            ColorTransfer: GetString(stream, "color_transfer"),
            ColorSpace: GetString(stream, "color_space"),
            ColorRange: GetString(stream, "color_range"),
            BitsPerRawSample: GetInt(stream, "bits_per_raw_sample"),
            HdrMetadata: ParseHdrMetadata(stream))
        {
            MetadataKeys = ParseMetadataKeys(stream),
            Details = ParseDetails(stream),
            HasDolbyVision = HasSideData(stream, "DOVI configuration record"),
        };
    }

    private static int? ParseRotation(JsonElement stream)
    {
        // Check tags.rotate (older ffprobe format)
        if (stream.TryGetProperty("tags", out var tags))
        {
            var rotate = GetString(tags, "rotate");
            if (rotate is not null && int.TryParse(rotate, out var tagRotation))
                return tagRotation;
        }

        // Check side_data_list for display matrix rotation (newer format)
        if (stream.TryGetProperty("side_data_list", out var sideDataList))
        {
            foreach (var sideData in sideDataList.EnumerateArray())
            {
                if (GetString(sideData, "side_data_type") == "Display Matrix"
                    && GetInt(sideData, "rotation") is { } rotation)
                {
                    return rotation;
                }
            }
        }

        return null;
    }

    private static HdrMetadata? ParseHdrMetadata(JsonElement stream)
    {
        if (!stream.TryGetProperty("side_data_list", out var sideDataList))
            return null;

        int? maxCll = null;
        int? maxFall = null;
        MasteringDisplayMetadata? mastering = null;

        foreach (var sideData in sideDataList.EnumerateArray())
        {
            var type = GetString(sideData, "side_data_type");

            if (type == "Content light level metadata")
            {
                maxCll = GetInt(sideData, "max_content");
                maxFall = GetInt(sideData, "max_average");
            }
            else if (type == "Mastering display metadata")
            {
                mastering = new MasteringDisplayMetadata(
                    RedX: GetString(sideData, "red_x") ?? "",
                    RedY: GetString(sideData, "red_y") ?? "",
                    GreenX: GetString(sideData, "green_x") ?? "",
                    GreenY: GetString(sideData, "green_y") ?? "",
                    BlueX: GetString(sideData, "blue_x") ?? "",
                    BlueY: GetString(sideData, "blue_y") ?? "",
                    WhitePointX: GetString(sideData, "white_point_x") ?? "",
                    WhitePointY: GetString(sideData, "white_point_y") ?? "",
                    MinLuminance: GetString(sideData, "min_luminance") ?? "",
                    MaxLuminance: GetString(sideData, "max_luminance") ?? "");
            }
        }

        if (maxCll is null && maxFall is null && mastering is null)
            return null;

        return new HdrMetadata(maxCll, maxFall, mastering);
    }

    private static AudioStreamInfo ParseAudioStream(JsonElement stream)
    {
        return new AudioStreamInfo(
            Index: GetInt(stream, "index") ?? 0,
            CodecName: GetString(stream, "codec_name"),
            BitRate: GetInt(stream, "bit_rate"),
            Channels: GetInt(stream, "channels"),
            SampleRate: GetInt(stream, "sample_rate"),
            Language: GetTag(stream, "language"))
        {
            ChannelLayout = GetString(stream, "channel_layout"),
            MetadataKeys = ParseMetadataKeys(stream),
            Details = ParseDetails(stream),
        };
    }

    private static SubtitleStreamInfo ParseSubtitleStream(JsonElement stream)
    {
        return new SubtitleStreamInfo(
            Index: GetInt(stream, "index") ?? 0,
            CodecName: GetString(stream, "codec_name"),
            Language: GetTag(stream, "language"))
        {
            Details = ParseDetails(stream),
        };
    }

    private static DataStreamInfo ParseDataStream(JsonElement stream)
    {
        return new DataStreamInfo(
            Index: GetInt(stream, "index") ?? 0,
            CodecName: GetString(stream, "codec_name"))
        {
            IsAttachment = GetString(stream, "codec_type") == "attachment",
            FileName = GetTag(stream, "filename"),
            MimeType = GetTag(stream, "mimetype"),
        };
    }

    private static StreamDetails ParseDetails(JsonElement stream)
    {
        var tags = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        if (stream.TryGetProperty("tags", out var tagElement) && tagElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in tagElement.EnumerateObject())
            {
                if (tag.Value.ValueKind == JsonValueKind.String)
                    tags[tag.Name] = tag.Value.GetString() ?? "";
            }
        }

        var disposition = StreamDisposition.None;
        if (stream.TryGetProperty("disposition", out var flags))
        {
            disposition = new StreamDisposition
            {
                Default = GetInt(flags, "default") == 1,
                Forced = GetInt(flags, "forced") == 1,
                HearingImpaired = GetInt(flags, "hearing_impaired") == 1,
                VisualImpaired = GetInt(flags, "visual_impaired") == 1,
                Comment = GetInt(flags, "comment") == 1,
                Original = GetInt(flags, "original") == 1,
            };
        }

        return new StreamDetails
        {
            Title = tags.GetValueOrDefault("title"),
            Language = tags.GetValueOrDefault("language"),
            Disposition = disposition,
            Profile = GetString(stream, "profile"),
            BitsPerRawSample = GetInt(stream, "bits_per_raw_sample"),
            DurationSeconds = GetDouble(stream, "duration"),
            Tags = tags.ToImmutable(),
        };
    }

    private static ImmutableArray<ChapterInfo> ParseChapters(JsonElement root)
    {
        if (!root.TryGetProperty("chapters", out var chapters) || chapters.ValueKind != JsonValueKind.Array)
            return [];

        var result = ImmutableArray.CreateBuilder<ChapterInfo>();
        foreach (var chapter in chapters.EnumerateArray())
        {
            result.Add(new ChapterInfo(
                StartSeconds: GetDouble(chapter, "start_time") ?? 0,
                EndSeconds: GetDouble(chapter, "end_time") ?? 0,
                Title: GetTagIgnoreCase(chapter, "title")));
        }
        return result.ToImmutable();
    }

    private static bool HasSideData(JsonElement stream, string sideDataType)
    {
        if (!stream.TryGetProperty("side_data_list", out var sideDataList))
            return false;

        foreach (var sideData in sideDataList.EnumerateArray())
        {
            if (GetString(sideData, "side_data_type") == sideDataType)
                return true;
        }
        return false;
    }

    internal static double? ParseFrameRate(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == "N/A" || value == "0/0")
            return null;

        var slashIndex = value.IndexOf('/');
        if (slashIndex < 0)
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) ? single : null;

        if (double.TryParse(value.AsSpan(0, slashIndex), NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(value.AsSpan(slashIndex + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0)
        {
            return numerator / denominator;
        }

        return null;
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return null;
    }

    private static int? GetInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        // ffprobe emits numeric fields as JSON strings
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt32(out var n) ? n : null;
        if (value.ValueKind == JsonValueKind.String)
            return int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        return null;
    }

    private static long? GetLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt64(out var n) ? n : null;
        if (value.ValueKind == JsonValueKind.String)
            return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        return null;
    }

    private static double? GetDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetDouble(out var n) ? n : null;
        if (value.ValueKind == JsonValueKind.String)
            return double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        return null;
    }

    private static string? GetTag(JsonElement stream, string tagName)
    {
        if (stream.TryGetProperty("tags", out var tags))
            return GetString(tags, tagName);
        return null;
    }

    // Vorbis comments (Ogg, FLAC) keep the writer's casing, for example TITLE.
    private static string? GetTagIgnoreCase(JsonElement element, string tagName)
    {
        if (!element.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var tag in tags.EnumerateObject())
        {
            if (tag.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase) && tag.Value.ValueKind == JsonValueKind.String)
                return tag.Value.GetString();
        }
        return null;
    }

    private static ImmutableArray<string> ParseMetadataKeys(JsonElement element)
    {
        if (!element.TryGetProperty("tags", out var tags)
            || tags.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return [.. tags.EnumerateObject().Select(tag => tag.Name)];
    }
}
