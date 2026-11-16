using System.Collections.Immutable;

using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;

namespace FFApp.Infrastructure;

public sealed class DefaultJobExecutionService(
    ICommandBuilder commandBuilder,
    IProcessRunner processRunner,
    IMatroskaPostProcessor matroskaPostProcessor,
    TimeProvider timeProvider,
    IFfmpegCapabilityService? capabilities = null) : IJobExecutionService
{
    /// <summary>
    /// Executes preparation, encoding and post-processing in order. Cancellation stops
    /// the active process; temporary image files and two-pass logs are removed in finally.
    /// A partially written output file is retained, matching failed encoding behavior.
    /// </summary>
    public async Task<JobResult> ExecuteAsync(
        JobDefinition job,
        SourceMediaInfo mediaInfo,
        Action<JobProgress>? progressCallback = null,
        Action<string>? logCallback = null,
        ExecutionPauseState? pauseState = null,
        CancellationToken ct = default)
    {
        if (capabilities?.Capabilities is { } available)
        {
            var missing = FfmpegFeatureValidation.Validate(job, mediaInfo, available);
            if (missing.Count > 0)
                throw new CommandBuildException(string.Join(Environment.NewLine, missing.Select(issue => issue.Message)));
        }
        var encodingCommands = commandBuilder.Build(job, mediaInfo);
        var postProcessCommand = matroskaPostProcessor.BuildCommand(job);
        var plannedCommands = postProcessCommand is null
            ? encodingCommands
            : [.. encodingCommands, postProcessCommand];
        var passCount = encodingCommands.Count(command => command.Protocol == ProcessProtocol.Ffmpeg);
        var expectedDuration = EffectiveOutputDuration(job, mediaInfo);
        var expectedFrameCount = EstimateOutputFrameCount(job, mediaInfo);
        var processResults = new List<ProcessExecutionResult>(passCount);
        var startedAt = timeProvider.GetTimestamp();
        var initialPausedTime = pauseState?.TotalPausedTime ?? TimeSpan.Zero;
        double ActiveElapsedSeconds() => Math.Max(0, (timeProvider.GetElapsedTime(startedAt)
            - ((pauseState?.TotalPausedTime ?? TimeSpan.Zero) - initialPausedTime)).TotalSeconds);

        // Emit initial progress
        progressCallback?.Invoke(new JobProgress(
            State: JobState.Running,
            PassIndex: 1,
            PassCount: passCount,
            ElapsedSeconds: 0,
            Percentage: 0));

        try
        {
            var completedPasses = 0;
            for (var i = 0; i < encodingCommands.Count; i++)
            {
                var isPreparation = encodingCommands[i].Protocol == ProcessProtocol.Plain;
                var passIndex = isPreparation ? 1 : ++completedPasses;

                Action<ProcessProgress>? wrappedProgress = progressCallback is not null
                    ? progress =>
                    {
                        progressCallback(new JobProgress(
                            State: JobState.Running,
                            PassIndex: passIndex,
                            PassCount: passCount,
                            ElapsedSeconds: ActiveElapsedSeconds(),
                            ProcessedSeconds: progress.ProcessedSeconds,
                            Percentage: progress.Percentage,
                            Speed: progress.Speed));
                    }
                : null;

                Action<string>? wrappedLog = logCallback is not null && isPreparation
                    ? line => logCallback($"[Preparing images] {line}")
                    : logCallback is not null && passCount > 1
                    ? line => logCallback($"[Pass {passIndex}] {line}")
                    : logCallback;

                var result = await processRunner.RunAsync(
                    encodingCommands[i],
                    expectedDuration,
                    expectedFrameCount,
                    wrappedProgress,
                    wrappedLog,
                    pauseState,
                    ct).ConfigureAwait(false);

                processResults.Add(result);

                if (result.State != JobState.Completed)
                    break;
            }

            if (ResolveJobState(processResults) == JobState.Completed
                && postProcessCommand is not null)
            {
                logCallback?.Invoke("[Post-processing] Recalculating Matroska track statistics...");
                var result = await processRunner.RunAsync(
                    postProcessCommand,
                    logCallback: logCallback,
                    pauseState: pauseState,
                    ct: ct).ConfigureAwait(false);
                processResults.Add(result);
            }
        }
        finally
        {
            CleanupTwoPassLogs(job.OutputPath);
            foreach (var path in encodingCommands.SelectMany(command => command.TemporaryFiles))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException ex)
                {
                    logCallback?.Invoke($"[Warning] Could not delete temporary image {path}: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    logCallback?.Invoke($"[Warning] Could not delete temporary image {path}: {ex.Message}");
                }
            }
        }

        var finalState = ResolveJobState(processResults);

        // Emit final progress
        progressCallback?.Invoke(new JobProgress(
            State: finalState,
            PassIndex: passCount,
            PassCount: passCount,
            ElapsedSeconds: ActiveElapsedSeconds(),
            Percentage: finalState == JobState.Completed ? 100.0 : null));

        return new JobResult(
            Job: job,
            State: finalState,
            Commands: [.. plannedCommands],
            ProcessResults: [.. processResults],
            ElapsedSeconds: ActiveElapsedSeconds(),
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
        var passIndex = 0;
        for (var i = 0; i < processResults.Count; i++)
        {
            var result = processResults[i];
            var output = result.StderrOutput;
            if (!string.IsNullOrEmpty(output))
            {
                var label = result.Command.Protocol == ProcessProtocol.Plain
                    ? result.Command.TemporaryFiles.Length > 0 ? "Preparing images" : "Post-processing"
                    : $"Pass {++passIndex}";
                parts.Add($"[{label}]{Environment.NewLine}{output}");
            }
            else if (result.Command.Protocol == ProcessProtocol.Ffmpeg)
            {
                passIndex++;
            }
        }

        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    internal static void CleanupTwoPassLogs(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        var stem = Path.GetFileNameWithoutExtension(outputPath);
        var pattern = $".{stem}-{DefaultCommandBuilder.TwoPassLogSuffix}*";

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
