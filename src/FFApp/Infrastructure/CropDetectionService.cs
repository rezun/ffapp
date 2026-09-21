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

    public async Task<CropRect> DetectAsync(
        SourceMediaInfo mediaInfo,
        TimeSpan position,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mediaInfo);

        var videoStream = mediaInfo.PrimaryVideoStream
            ?? throw new CropDetectionException("The source does not contain a video stream.");
        var seekPosition = Math.Max(0, position.TotalSeconds)
            .ToString("0.###", CultureInfo.InvariantCulture);

        var arguments = ImmutableArray.Create(
            "-hide_banner",
            "-ss", seekPosition,
            "-i", mediaInfo.SourcePath,
            "-map", $"0:{videoStream.Index}",
            "-frames:v", "1",
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

        return ParseCrop(result.StderrOutput)
            ?? throw new CropDetectionException("No crop area was detected in the current frame.");
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
