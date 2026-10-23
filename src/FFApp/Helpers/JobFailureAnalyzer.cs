using System.Globalization;
using System.Text.RegularExpressions;
using FFApp.Core.Models;

namespace FFApp.Helpers;

/// <summary>Extracts a failure from the failed process, rather than warnings or cascading errors.</summary>
internal static partial class JobFailureAnalyzer
{
    private static readonly int MaximumDetailLines = 6;
    private static readonly int MaximumMessageLength = 240;

    public static JobFailure Analyze(JobResult? result, string? failureMessage = null)
    {
        var failedProcess = result?.ProcessResults.FirstOrDefault(p => p.State == JobState.Failed);
        var output = failedProcess?.StderrOutput ?? result?.LogOutput ?? "";
        var postProcessing = failedProcess?.Command.Protocol == ProcessProtocol.Plain;
        var title = postProcessing ? "Post-processing failed" : "Encoding failed";
        var context = BuildContext(result, failedProcess);

        if (!string.IsNullOrWhiteSpace(failureMessage))
            return new JobFailure(title, failureMessage, "Check the reported error before starting a new job.", context, failureMessage);

        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();

        // These concrete causes take precedence over generic encoder and thread shutdown messages.
        foreach (var line in lines)
        {
            var sampleRate = UnsupportedSampleRateRegex().Match(line);
            if (!postProcessing && sampleRate.Success
                && int.TryParse(sampleRate.Groups["rate"].Value, CultureInfo.InvariantCulture, out var rate)
                && rate > 0)
            {
                var encoder = sampleRate.Groups["encoder"].Value;
                var suggestion = string.Equals(encoder, "libopus", StringComparison.OrdinalIgnoreCase)
                    ? "Set Audio → Sample rate to 48 kHz, then create a new job with the corrected settings."
                    : "Choose a sample rate supported by the audio encoder, then create a new job.";
                return new JobFailure("Unsupported audio sample rate",
                    $"{encoder} cannot encode audio at {FormatSampleRate(rate)}.",
                    suggestion, context, line);
            }
        }

        foreach (var (pattern, cause, suggestion) in KnownCauses())
        {
            var line = lines.FirstOrDefault(line => line.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (line is not null)
                return new JobFailure(title, cause, suggestion, context, line);
        }

        var errors = lines.Where(IsErrorLine)
            .Select(StripPrefix).Distinct(StringComparer.Ordinal).ToArray();
        var primary = errors.FirstOrDefault(line => !IsCascadingError(line))
            ?? errors.FirstOrDefault();
        var message = primary is null
            ? "The process stopped without reporting a specific cause."
            : primary.Length > MaximumMessageLength ? primary[..MaximumMessageLength] + "…" : primary;
        var details = primary is null
            ? string.Join(Environment.NewLine, lines.TakeLast(MaximumDetailLines))
            : string.Join(Environment.NewLine, new[] { primary }.Concat(errors)
                .Distinct(StringComparer.Ordinal).Take(MaximumDetailLines));

        return new JobFailure(title, message,
            "Review the error details below. The full log is available for further diagnosis.",
            context, details);
    }

    private static IReadOnlyList<(string Pattern, string Cause, string Suggestion)> KnownCauses() =>
    [
        ("No space left on device", "The output drive has no free space left.", "Free space on the output drive or choose another output folder."),
        ("Permission denied", "The process does not have permission to access a file or folder.", "Check access permissions for the source and output folders."),
        ("Read-only file system", "The output drive is read-only.", "Choose a writable output folder."),
        ("Unknown encoder", "The selected encoder is unavailable in this FFmpeg installation.", "Choose another codec or configure an FFmpeg build that includes this encoder."),
        ("No such file or directory", "A required file or folder could not be found.", "Check that the source file and output folder exist and any external drive is connected."),
        ("Invalid data found when processing input", "The input could not be read as valid media.", "Check that the source file is complete and can be played."),
    ];

    private static string BuildContext(JobResult? result, ProcessExecutionResult? process)
    {
        if (process is null || result is null)
            return "";
        var tool = Path.GetFileNameWithoutExtension(process.Command.ExecutablePath);
        if (process.Command.Protocol == ProcessProtocol.Plain)
            return $"{tool} · Post-processing · Exit code {process.ReturnCode}";
        var encodingResults = result.ProcessResults.Where(p => p.Command.Protocol == ProcessProtocol.Ffmpeg).ToArray();
        var pass = Array.IndexOf(encodingResults, process) + 1;
        var total = result.Commands.Count(c => c.Protocol == ProcessProtocol.Ffmpeg);
        var passLabel = total > 1 ? $" · Pass {pass}/{total}" : "";
        return $"{tool}{passLabel} · Exit code {process.ReturnCode}";
    }

    private static string FormatSampleRate(int rate) =>
        (rate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " kHz";

    private static string StripPrefix(string line)
    {
        while (line.StartsWith("[", StringComparison.Ordinal))
        {
            var end = line.IndexOf(']');
            if (end < 0)
                break;
            line = line[(end + 1)..].TrimStart();
        }
        return line;
    }

    private static bool IsErrorLine(string line) =>
        !line.Contains("[info]", StringComparison.OrdinalIgnoreCase)
        && !line.Contains("[warn", StringComparison.OrdinalIgnoreCase)
        && !line.Contains("Could not find codec parameters", StringComparison.OrdinalIgnoreCase)
        && !line.StartsWith("frame=", StringComparison.Ordinal)
        && (line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || line.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || line.Contains("could not", StringComparison.OrdinalIgnoreCase)
            || line.Contains("unable to", StringComparison.OrdinalIgnoreCase)
            || line.Contains("nothing was written", StringComparison.OrdinalIgnoreCase));

    private static bool IsCascadingError(string line) =>
        line.Contains("Error while opening encoder", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Error sending frames", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Task finished with error code", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Terminating thread", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Could not open encoder before EOF", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Nothing was written", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Conversion failed", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"Specified sample rate (?<rate>\d+) is not supported by (?:the )?(?<encoder>\S+) encoder", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnsupportedSampleRateRegex();
}
