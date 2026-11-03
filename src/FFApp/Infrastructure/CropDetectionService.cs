using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;

namespace FFApp.Infrastructure;

public sealed partial class CropDetectionService(
    ExecutablePathsConfig pathsConfig,
    IProcessRunner processRunner) : ICropDetectionService
{
    // Values above 1 are interpreted as raw pixel levels. Use the normalized 24/255
    // threshold so black detection scales correctly for 8-, 10-, and 12-bit sources.
    private const string CropDetectFilter =
        "cropdetect=limit=0.0941176:round=2:reset=0:skip=0";

    // Suggestions sample evenly spaced positions, skipping the very start and end where
    // logos and credits are common. With reset=0 each sample reports the union of its frames.
    private static readonly int SuggestionSampleCount = 6;
    private static readonly int SuggestionFramesPerSample = 3;

    // Ignore edge slivers smaller than this; they are not worth a suggestion.
    private static readonly int MinimumBarPixels = 8;

    // Dark sources can make every sample collapse to a small box. Never suggest a crop
    // that keeps less than this fraction of the frame area.
    private static readonly double MinimumKeptAreaFraction = 0.5;

    public async Task<CropRect> DetectAsync(
        SourceMediaInfo mediaInfo,
        TimeSpan position,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mediaInfo);

        var videoStream = mediaInfo.PrimaryVideoStream
            ?? throw new CropDetectionException("The source does not contain a video stream.");

        return await RunCropDetectAsync(mediaInfo, videoStream, position, frameCount: 1, ct)
            ?? throw new CropDetectionException("No crop area was detected in the current frame.");
    }

    public async Task<CropRect?> SuggestAsync(
        SourceMediaInfo mediaInfo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mediaInfo);

        if (mediaInfo.PrimaryVideoStream is not { Width: int width and > 0, Height: int height and > 0 } videoStream
            || mediaInfo.DurationSeconds is not { } duration
            || !double.IsFinite(duration)
            || duration <= 0)
        {
            return null;
        }

        CropRect? union = null;
        var successfulSamples = 0;
        for (var sample = 1; sample <= SuggestionSampleCount; sample++)
        {
            var position = TimeSpan.FromSeconds(duration * sample / (SuggestionSampleCount + 1));
            CropRect? crop;
            try
            {
                crop = await RunCropDetectAsync(
                    mediaInfo, videoStream, position, SuggestionFramesPerSample, ct);
            }
            catch (CropDetectionException)
            {
                // A sample that cannot be decoded contributes no evidence; the remaining
                // samples still decide whether a suggestion is safe.
                continue;
            }

            if (crop is null)
                continue;

            successfulSamples++;
            union = union is null ? crop : Union(union, crop);
        }

        if (union is null || successfulSamples * 2 < SuggestionSampleCount)
            return null;

        return IsWorthSuggesting(union, width, height) ? union : null;
    }

    internal static CropRect Union(CropRect a, CropRect b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new CropRect(left, top, right - left, bottom - top);
    }

    internal static bool IsWorthSuggesting(CropRect crop, int sourceWidth, int sourceHeight)
    {
        if (crop.X < 0 || crop.Y < 0
            || crop.X + crop.Width > sourceWidth
            || crop.Y + crop.Height > sourceHeight)
        {
            return false;
        }

        var removesBars = sourceWidth - crop.Width >= MinimumBarPixels
            || sourceHeight - crop.Height >= MinimumBarPixels;
        var keptArea = (double)crop.Width * crop.Height / ((double)sourceWidth * sourceHeight);
        return removesBars && keptArea >= MinimumKeptAreaFraction;
    }

    private async Task<CropRect?> RunCropDetectAsync(
        SourceMediaInfo mediaInfo,
        VideoStreamInfo videoStream,
        TimeSpan position,
        int frameCount,
        CancellationToken ct)
    {
        var seekPosition = Math.Max(0, position.TotalSeconds)
            .ToString("0.###", CultureInfo.InvariantCulture);

        var arguments = ImmutableArray.Create(
            "-hide_banner",
            "-ss", seekPosition,
            "-i", mediaInfo.SourcePath,
            "-map", $"0:{videoStream.Index}",
            "-frames:v", frameCount.ToString(CultureInfo.InvariantCulture),
            "-vf", CropDetectFilter,
            "-an", "-sn", "-dn",
            "-f", "null", "-");

        var command = new GeneratedCommand(
            pathsConfig.FfmpegPath,
            arguments,
            $"{pathsConfig.FfmpegPath} crop detection");

        ProcessExecutionResult result;
        try
        {
            result = await processRunner.RunAsync(command, ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CropDetectionException("Failed to run FFmpeg crop detection.", ex);
        }

        if (result.State == JobState.Canceled)
            throw new OperationCanceledException(ct);

        if (result.State != JobState.Completed || result.ReturnCode != 0)
            throw new CropDetectionException("FFmpeg could not analyze the current frame.");

        return ParseCrop(result.StderrOutput);
    }

    internal static CropRect? ParseCrop(string output)
    {
        var matches = CropResultRegex().Matches(output);
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var match = matches[index];
            if (int.TryParse(match.Groups["width"].ValueSpan, CultureInfo.InvariantCulture, out var width)
                && int.TryParse(match.Groups["height"].ValueSpan, CultureInfo.InvariantCulture, out var height)
                && int.TryParse(match.Groups["x"].ValueSpan, CultureInfo.InvariantCulture, out var x)
                && int.TryParse(match.Groups["y"].ValueSpan, CultureInfo.InvariantCulture, out var y)
                && width > 0
                && height > 0)
            {
                return new CropRect(x, y, width, height);
            }
        }

        return null;
    }

    [GeneratedRegex(@"crop=(?<width>\d+):(?<height>\d+):(?<x>\d+):(?<y>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CropResultRegex();
}
