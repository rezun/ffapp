using System.Diagnostics;
using System.Globalization;

using FFApp.Core.Models;
using FFApp.Core.Services;

namespace FFApp.Infrastructure;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessExecutionResult> RunAsync(
        GeneratedCommand command,
        double? expectedDurationSeconds = null,
        int? expectedFrameCount = null,
        Action<ProcessProgress>? progressCallback = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default)
    {
        var arguments = new List<string>(command.Arguments.Length + 3)
        {
            "-progress", "pipe:1", "-nostats",
        };
        arguments.AddRange(command.Arguments);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.ExecutablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);

        var stderrLines = new List<string>();
        var progressPacket = new Dictionary<string, string>();
        var stopwatch = Stopwatch.StartNew();

        using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* process already exited */ }
        });

        process.Start();

        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var separatorIndex = line.IndexOf('=');
                if (separatorIndex < 0)
                    continue;

                var key = line[..separatorIndex].Trim();
                var value = line[(separatorIndex + 1)..].Trim();
                progressPacket[key] = value;

                if (key == "progress")
                {
                    var progress = ParseProgressUpdate(
                        progressPacket,
                        stopwatch.Elapsed.TotalSeconds,
                        expectedDurationSeconds,
                        expectedFrameCount);
                    progressCallback?.Invoke(progress);
                    progressPacket.Clear();
                }
            }
        }, CancellationToken.None);

        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                stderrLines.Add(line);
                logCallback?.Invoke(line);
            }
        }, CancellationToken.None);

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        stopwatch.Stop();

        var state = ct.IsCancellationRequested
            ? JobState.Canceled
            : process.ExitCode == 0 ? JobState.Completed : JobState.Failed;

        return new ProcessExecutionResult(
            Command: command,
            State: state,
            ReturnCode: process.ExitCode,
            ElapsedSeconds: stopwatch.Elapsed.TotalSeconds,
            StderrOutput: string.Join(Environment.NewLine, stderrLines));
    }

    // --- Static helpers ---

    internal static ProcessProgress ParseProgressUpdate(
        Dictionary<string, string> packet,
        double elapsedSeconds,
        double? expectedDuration,
        int? expectedFrameCount)
    {
        double? processedSeconds = null;
        double? speed = null;
        int? currentFrame = null;

        if (packet.TryGetValue("out_time", out var outTime))
            processedSeconds = ParseTimestamp(outTime);

        processedSeconds ??= packet.TryGetValue("out_time_us", out var outTimeUs)
            && double.TryParse(outTimeUs, CultureInfo.InvariantCulture, out var microseconds)
                ? microseconds / 1_000_000.0
                : null;

        processedSeconds ??= packet.TryGetValue("out_time_ms", out var outTimeMs)
            && double.TryParse(outTimeMs, CultureInfo.InvariantCulture, out var milliseconds)
                ? milliseconds / 1_000.0
                : null;

        if (packet.TryGetValue("speed", out var speedValue))
            speed = ParseSpeed(speedValue);

        if (packet.TryGetValue("frame", out var frameValue)
            && int.TryParse(frameValue, CultureInfo.InvariantCulture, out var parsedFrame))
        {
            currentFrame = parsedFrame;
        }

        var percentage = CalculatePercentage(
            processedSeconds, expectedDuration, currentFrame, expectedFrameCount);

        return new ProcessProgress(
            ElapsedSeconds: elapsedSeconds,
            ProcessedSeconds: processedSeconds,
            Percentage: percentage,
            Speed: speed);
    }

    internal static double? ParseTimestamp(string value)
    {
        // Format: HH:MM:SS.mmm (e.g. "01:23:45.678")
        var span = value.AsSpan().Trim();

        var firstColon = span.IndexOf(':');
        if (firstColon < 0)
            return null;

        var rest = span[(firstColon + 1)..];
        var secondColon = rest.IndexOf(':');
        if (secondColon < 0)
            return null;

        if (!int.TryParse(span[..firstColon], CultureInfo.InvariantCulture, out var hours))
            return null;
        if (!int.TryParse(rest[..secondColon], CultureInfo.InvariantCulture, out var minutes))
            return null;
        if (!double.TryParse(rest[(secondColon + 1)..], CultureInfo.InvariantCulture, out var seconds))
            return null;

        return hours * 3600.0 + minutes * 60.0 + seconds;
    }

    internal static double? ParseSpeed(string value)
    {
        var span = value.AsSpan().Trim();
        if (span.EndsWith("x", StringComparison.OrdinalIgnoreCase))
            span = span[..^1];

        return double.TryParse(span, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    internal static double? CalculatePercentage(
        double? processedSeconds,
        double? expectedDuration,
        int? currentFrame,
        int? expectedFrameCount)
    {
        // Prefer time-based calculation
        if (processedSeconds is > 0 && expectedDuration is > 0)
            return Math.Min(processedSeconds.Value / expectedDuration.Value * 100.0, 100.0);

        // Fallback to frame-based
        if (currentFrame is > 0 && expectedFrameCount is > 0)
            return Math.Min((double)currentFrame.Value / expectedFrameCount.Value * 100.0, 100.0);

        return null;
    }
}
