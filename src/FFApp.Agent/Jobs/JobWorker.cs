using System.Text;

using FFApp.Agent.Storage;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Remote.Contracts;
using Microsoft.Extensions.Options;

namespace FFApp.Agent.Jobs;

/// <summary>
/// Runs waiting jobs one after another with the same <see cref="IJobExecutionService"/> the
/// desktop app uses. Each job gets its own work directory for temporary files.
/// </summary>
public sealed class JobWorker(
    AgentJobQueue queue,
    IJobExecutionService execution,
    AgentPaths paths,
    IOptions<AgentOptions> options,
    ILogger<JobWorker> logger) : BackgroundService
{
    private static readonly int FailureLogLines = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.MaxConcurrentJobs > 1)
            logger.LogWarning("MaxConcurrentJobs {Count} is not supported yet; jobs run one at a time", options.Value.MaxConcurrentJobs);

        while (!stoppingToken.IsCancellationRequested)
        {
            AgentJobRecord job;
            ActiveExecution active;
            try
            {
                (job, active) = await queue.WaitForNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            await RunAsync(job, active, stoppingToken);
        }
    }

    /// <summary>
    /// Executes one job. Every outcome, including unexpected exceptions, ends in
    /// <see cref="AgentJobQueue.Finish"/>, so the encoder is never left blocked.
    /// </summary>
    private async Task RunAsync(AgentJobRecord record, ActiveExecution active, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(active.Cancellation.Token, stoppingToken);
        var workDirectory = paths.JobWork(record.JobId);
        Directory.CreateDirectory(workDirectory);
        var logLock = new Lock();
        var tail = new Queue<string>();

        await using var log = new StreamWriter(paths.JobLog(record.JobId), append: true, Encoding.UTF8) { AutoFlush = true };
        void Log(string line)
        {
            lock (logLock)
            {
                log.WriteLine(line);
                tail.Enqueue(line);
                while (tail.Count > FailureLogLines)
                    tail.Dequeue();
            }
            queue.ReportLog(record.JobId, record.ClientId, line);
        }

        var state = AgentJobState.Failed;
        string? failure = null;
        double activeSeconds = 0;
        try
        {
            var (job, media) = queue.PrepareExecution(record);
            logger.LogInformation("Starting job {JobId}", record.JobId);
            var result = await execution.ExecuteAsync(
                job, media,
                progress => queue.ReportProgress(record.JobId, progress),
                Log,
                active.Pause,
                workDirectory,
                linked.Token);
            activeSeconds = result.ElapsedSeconds;

            state = result.State switch
            {
                JobState.Completed => AgentJobState.Completed,
                JobState.Canceled => AgentJobState.Canceled,
                JobState.Failed => AgentJobState.Failed,
                _ => throw new InvalidOperationException($"Unexpected execution state {result.State}."),
            };
            if (state == AgentJobState.Failed)
                failure = FailureMessage(result, tail);
            if (stoppingToken.IsCancellationRequested && state == AgentJobState.Canceled)
            {
                state = AgentJobState.Failed;
                failure = AgentJobQueue.InterruptedMessage;
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            state = stoppingToken.IsCancellationRequested ? AgentJobState.Failed : AgentJobState.Canceled;
            if (state == AgentJobState.Failed)
                failure = AgentJobQueue.InterruptedMessage;
        }
        catch (CommandBuildException ex)
        {
            failure = ex.Message;
            Log($"[Error] {ex.Message}");
        }
        catch (Exception ex)
        {
            // Top-level boundary of a job: any failure must end the job, not the worker.
            logger.LogError(ex, "Job {JobId} failed unexpectedly", record.JobId);
            failure = ex.Message;
            Log($"[Error] {ex.Message}");
        }
        finally
        {
            queue.Finish(record.JobId, state, failure, activeSeconds);
        }
    }

    private static string FailureMessage(JobResult result, IEnumerable<string> tail)
    {
        var failed = result.ProcessResults.LastOrDefault(process => process.State == JobState.Failed);
        var exitCode = failed?.ReturnCode;
        var lastLine = tail.LastOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();
        var prefix = exitCode is { } code ? $"FFmpeg exited with code {code}." : "Encoding failed.";
        return lastLine is null ? prefix : $"{prefix} {lastLine}";
    }
}
