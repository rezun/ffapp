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

            if (root.TryGetProperty("format", out var format))
            {
                containerFormat = GetString(format, "format_name");
                durationSeconds = GetDouble(format, "duration");
                bitRate = GetInt(format, "bit_rate");
            }

            var videoStreams = ImmutableArray.CreateBuilder<VideoStreamInfo>();
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
                VideoStreams = videoStreams.ToImmutable(),
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
            FrameCount: GetInt(stream, "nb_frames"));
    }

    private static AudioStreamInfo ParseAudioStream(JsonElement stream)
    {
        return new AudioStreamInfo(
            Index: GetInt(stream, "index") ?? 0,
            CodecName: GetString(stream, "codec_name"),
            BitRate: GetInt(stream, "bit_rate"),
            Channels: GetInt(stream, "channels"),
            Language: GetTag(stream, "language"));
    }

    private static SubtitleStreamInfo ParseSubtitleStream(JsonElement stream)
    {
        return new SubtitleStreamInfo(
            Index: GetInt(stream, "index") ?? 0,
            CodecName: GetString(stream, "codec_name"),
            Language: GetTag(stream, "language"));
    }

    private static DataStreamInfo ParseDataStream(JsonElement stream)
    {
        return new DataStreamInfo(
            Index: GetInt(stream, "index") ?? 0,
            CodecName: GetString(stream, "codec_name"));
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
}
