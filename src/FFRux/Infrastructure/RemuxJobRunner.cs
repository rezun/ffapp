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
    private const double TrialSeconds = 1;
    private const int ErrorLineCount = 4;

    // A test remux of one second normally ends at once. Sparse subtitle tracks can make FFmpeg read
    // far into a large file, so a slow test is skipped rather than treated as a failure.
    private static readonly TimeSpan TrialTimeout = TimeSpan.FromSeconds(30);

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
        progress?.Invoke(new RemuxProgress(RemuxStage.Testing, null, elapsed()));
        var trial = await RunTrialAsync(plan, log, ct);
        if (ct.IsCancellationRequested)
            return new RemuxResult(RemuxOutcome.Canceled, outputPath, elapsed());
        if (trial is { } trialError)
            return new RemuxResult(RemuxOutcome.Failed, outputPath, elapsed(), trialError);

        var partialPath = OutputPaths.PartialFile(outputPath);
        DeleteIfExists(partialPath);
        try
        {
            var commands = commandBuilder.Build(plan, partialPath);
            log($"$ {commands.Ffmpeg.DisplayString}");
            var expected = RemuxCommandBuilder.ExpectedDurationSeconds(plan);
            var remux = await processRunner.RunAsync(
                commands.Ffmpeg,
                expectedDurationSeconds: expected,
                progressCallback: p => progress?.Invoke(new RemuxProgress(RemuxStage.Remuxing, p.Percentage, elapsed())),
                logCallback: log,
                ct: ct);

            if (remux.State == JobState.Canceled || ct.IsCancellationRequested)
                return Canceled();
            if (remux.State != JobState.Completed)
                return Failed($"FFmpeg stopped with exit code {remux.ReturnCode}.", remux.StderrOutput);

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

    /// <summary>Remuxes one second into a temporary file. Returns the error, or null when the test passed or was skipped.</summary>
    private async Task<string?> RunTrialAsync(RemuxPlan plan, Action<string> log, CancellationToken ct)
    {
        var trialPath = Path.Combine(Path.GetTempPath(), $"ffrux-test-{Guid.NewGuid():N}{plan.FormatInfo.Extension}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TrialTimeout);
        try
        {
            var command = commandBuilder.Build(plan, trialPath, TrialSeconds).Ffmpeg;
            log($"$ {command.DisplayString}");
            ProcessExecutionResult result;
            try
            {
                result = await processRunner.RunAsync(command, logCallback: log, ct: timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return TrialTimedOut();
            }
            if (ct.IsCancellationRequested)
                return null;
            if (result.State == JobState.Canceled)
                return TrialTimedOut();
            if (result.State != JobState.Completed)
                return Describe("FFmpeg rejected this combination in a test remux, so the job did not start.", result.StderrOutput);

            log("[FFRux] Test remux passed.");
            return null;
        }
        finally
        {
            DeleteIfExists(trialPath);
        }

        string? TrialTimedOut()
        {
            log("[FFRux] The test remux took too long and was skipped.");
            return null;
        }
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
