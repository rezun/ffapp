using System.Collections.Immutable;
using System.Diagnostics;

using FFApp.Core.Models;
using FFApp.Core.Services;

namespace FFApp.Infrastructure;

public sealed class DefaultJobExecutionService(
    ICommandBuilder commandBuilder,
    IProcessRunner processRunner) : IJobExecutionService
{
    public async Task<JobResult> ExecuteAsync(
        JobDefinition job,
        SourceMediaInfo mediaInfo,
        Action<JobProgress>? progressCallback = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default)
    {
        var commands = commandBuilder.Build(job, mediaInfo);
        var passCount = commands.Count;
        var expectedDuration = EffectiveOutputDuration(job, mediaInfo);
        var expectedFrameCount = EstimateOutputFrameCount(job, mediaInfo);
        var processResults = new List<ProcessExecutionResult>(passCount);
        var stopwatch = Stopwatch.StartNew();

        // Emit initial progress
        progressCallback?.Invoke(new JobProgress(
            State: JobState.Running,
            PassIndex: 1,
            PassCount: passCount,
            ElapsedSeconds: 0,
            Percentage: 0));

        try
        {
            for (var i = 0; i < passCount; i++)
            {
                var passIndex = i + 1;

                Action<ProcessProgress>? wrappedProgress = progressCallback is not null
                    ? progress =>
                    {
                        progressCallback(new JobProgress(
                            State: JobState.Running,
                            PassIndex: passIndex,
                            PassCount: passCount,
                            ElapsedSeconds: stopwatch.Elapsed.TotalSeconds,
                            ProcessedSeconds: progress.ProcessedSeconds,
                            Percentage: progress.Percentage,
                            Speed: progress.Speed));
                    }
                    : null;

                Action<string>? wrappedLog = logCallback is not null && passCount > 1
                    ? line => logCallback($"[Pass {passIndex}] {line}")
                    : logCallback;

                var result = await processRunner.RunAsync(
                    commands[i],
                    expectedDuration,
                    expectedFrameCount,
                    wrappedProgress,
                    wrappedLog,
                    ct).ConfigureAwait(false);

                processResults.Add(result);

                if (result.State != JobState.Completed)
                    break;
            }
        }
        finally
        {
            CleanupTwoPassLogs(job.OutputPath);
        }

        stopwatch.Stop();
        var finalState = ResolveJobState(processResults);

        // Emit final progress
        progressCallback?.Invoke(new JobProgress(
            State: finalState,
            PassIndex: passCount,
            PassCount: passCount,
            ElapsedSeconds: stopwatch.Elapsed.TotalSeconds,
            Percentage: finalState == JobState.Completed ? 100.0 : null));

        return new JobResult(
            Job: job,
            State: finalState,
            Commands: [.. commands],
            ProcessResults: [.. processResults],
            ElapsedSeconds: stopwatch.Elapsed.TotalSeconds,
            LogOutput: CombineProcessLogs(processResults));
    }

    // --- Static helpers ---

    internal static double? EffectiveOutputDuration(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var totalDuration = mediaInfo.DurationSeconds;
        if (totalDuration is null)
            return null;

        var start = job.TrimStartSeconds ?? 0;
        var end = job.TrimEndSeconds ?? totalDuration.Value;
        var duration = end - start;
        return duration > 0 ? duration : null;
    }

    internal static int? EstimateOutputFrameCount(JobDefinition job, SourceMediaInfo mediaInfo)
    {
        var video = mediaInfo.PrimaryVideoStream;
        if (video is null)
            return null;

        var duration = EffectiveOutputDuration(job, mediaInfo);

        // Only use source frame count if we're not trimming
        if (job.TrimStartSeconds is null && job.TrimEndSeconds is null && video.FrameCount is > 0)
            return video.FrameCount;

        if (video.FrameRate is > 0 && duration is > 0)
            return (int)(video.FrameRate.Value * duration.Value);

        return null;
    }

    internal static JobState ResolveJobState(List<ProcessExecutionResult> processResults)
    {
        if (processResults.Count == 0)
            return JobState.Failed;

        var lastResult = processResults[^1];
        return lastResult.State;
    }

    internal static string CombineProcessLogs(List<ProcessExecutionResult> processResults)
    {
        if (processResults.Count == 1)
            return processResults[0].StderrOutput;

        var parts = new List<string>(processResults.Count);
        for (var i = 0; i < processResults.Count; i++)
        {
            var output = processResults[i].StderrOutput;
            if (!string.IsNullOrEmpty(output))
                parts.Add($"[Pass {i + 1}]{Environment.NewLine}{output}");
        }

        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    internal static void CleanupTwoPassLogs(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        var stem = Path.GetFileNameWithoutExtension(outputPath);
        var pattern = $".{stem}-ffpass*";

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern))
                File.Delete(file);
        }
        catch (IOException)
        {
            // Best-effort cleanup; ignore failures.
        }
    }
}
