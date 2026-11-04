using System.Globalization;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure.Logging;
using FFRux.Core;
using FFRux.Core.Models;

namespace FFRux.Infrastructure;

public sealed class RemuxJobRunner(
    RemuxCommandBuilder commandBuilder,
    IProcessRunner processRunner,
    IJobLogStore jobLogs,
    TimeProvider timeProvider) : IRemuxJobRunner
{
    private const int ErrorLineCount = 4;

    public async Task<RemuxResult> RunAsync(
        RemuxPlan plan,
        Action<RemuxProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var outputPath = plan.OutputPath ?? throw new ArgumentException("The plan has no output path.", nameof(plan));
        var started = timeProvider.GetTimestamp();
        TimeSpan Elapsed() => timeProvider.GetElapsedTime(started);

        using var jobLog = StartJobLog(plan, outputPath);
        void Log(string line)
        {
            jobLog.WriteLine(line);
            log?.Invoke(line);
        }

        RemuxResult result;
        try
        {
            result = await RunLoggedAsync(plan, outputPath, Elapsed, progress, Log, ct);
        }
        catch (Exception ex)
        {
            // Rethrown unchanged; only recorded so the job log explains why it ends early.
            jobLog.WriteLine(ex.ToString());
            throw;
        }
        WriteJobLogResult(jobLog, result);
        return result with { LogPath = jobLog.FilePath };
    }

    private async Task<RemuxResult> RunLoggedAsync(
        RemuxPlan plan,
        string outputPath,
        Func<TimeSpan> elapsed,
        Action<RemuxProgress>? progress,
        Action<string> log,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (directory is null || !Directory.Exists(directory))
            return new RemuxResult(RemuxOutcome.Failed, outputPath, elapsed(), "The output folder doesn't exist.");

        try
        {
            return await RunStepsAsync(plan, outputPath, elapsed, progress, log, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // ProcessRunner throws instead of returning Canceled when the token fires between two steps.
            log("[FFRux] Canceled.");
            return new RemuxResult(RemuxOutcome.Canceled, outputPath, elapsed());
        }
    }

    private JobLog StartJobLog(RemuxPlan plan, string outputPath)
    {
        var jobLog = jobLogs.Start(Path.GetFileName(outputPath));
        foreach (var source in plan.Sources)
            jobLog.WriteField($"Source {source.Number}", source.Path);
        jobLog.WriteField("Output", outputPath);
        jobLog.WriteField("Format", plan.FormatInfo.Extension);
        jobLog.WriteLine("");
        return jobLog;
    }

    private static void WriteJobLogResult(JobLog jobLog, RemuxResult result)
    {
        jobLog.WriteLine("");
        jobLog.WriteField("Result", result.Outcome.ToString());
        jobLog.WriteField("Elapsed", result.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        foreach (var warning in result.Warnings)
            jobLog.WriteField("Warning", warning);
        if (result.Error is { Length: > 0 } error)
            jobLog.WriteField("Reason", error);
    }

    private async Task<RemuxResult> RunStepsAsync(
        RemuxPlan plan,
        string outputPath,
        Func<TimeSpan> elapsed,
        Action<RemuxProgress>? progress,
        Action<string> log,
        CancellationToken ct)
    {
        var partialPath = OutputPaths.PartialFile(outputPath);
        DeleteIfExists(partialPath);
        try
        {
            var commands = commandBuilder.Build(plan, partialPath);
            log($"$ {commands.Ffmpeg.DisplayString}");
            var expected = RemuxCommandBuilder.ExpectedDurationSeconds(plan);
            var wroteData = false;
            progress?.Invoke(new RemuxProgress(RemuxStage.Remuxing, null, elapsed()));
            var remux = await processRunner.RunAsync(
                commands.Ffmpeg,
                expectedDurationSeconds: expected,
                progressCallback: p =>
                {
                    wroteData |= p.ProcessedSeconds > 0;
                    progress?.Invoke(new RemuxProgress(RemuxStage.Remuxing, p.Percentage, elapsed()));
                },
                logCallback: log,
                ct: ct);

            if (remux.State == JobState.Canceled || ct.IsCancellationRequested)
                return Canceled();
            // FFmpeg checks the mapping, codecs and container while writing the header, so a rejected
            // combination fails before any data is written.
            if (remux.State != JobState.Completed)
                return Failed(wroteData
                    ? $"FFmpeg stopped with exit code {remux.ReturnCode}."
                    : "FFmpeg rejected this combination, so no output was written.", remux.StderrOutput);

            var warnings = new List<string>();
            if (commands.PostProcess is { } postProcess)
            {
                progress?.Invoke(new RemuxProgress(RemuxStage.PostProcessing, 100, elapsed()));
                log($"$ {postProcess.DisplayString}");
                var post = await processRunner.RunAsync(postProcess, logCallback: log, ct: ct);
                if (post.State == JobState.Canceled || ct.IsCancellationRequested)
                    return Canceled();
                if (post.State != JobState.Completed)
                {
                    const string warning = "mkvpropedit failed, so the track statistics were not updated.";
                    warnings.Add(warning);
                    log($"[FFRux] {warning}");
                }
            }

            File.Move(partialPath, outputPath, overwrite: true);
            log($"[FFRux] Wrote {outputPath}");
            return new RemuxResult(RemuxOutcome.Completed, outputPath, elapsed(), OutputBytes: new FileInfo(outputPath).Length)
            {
                Warnings = warnings,
            };
        }
        finally
        {
            DeleteIfExists(partialPath);
        }

        RemuxResult Canceled()
        {
            log("[FFRux] Canceled.");
            return new RemuxResult(RemuxOutcome.Canceled, outputPath, elapsed());
        }

        RemuxResult Failed(string reason, string stderr) =>
            new(RemuxOutcome.Failed, outputPath, elapsed(), Describe(reason, stderr));
    }

    internal static string Describe(string reason, string stderr)
    {
        var lines = stderr
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(ErrorLineCount)
            .ToList();
        return lines.Count == 0 ? reason : string.Join(Environment.NewLine, [reason, .. lines]);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover partial file is harmless; the next job deletes it before writing.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: never let cleanup hide the job's real result.
        }
    }
}
