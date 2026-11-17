using System.Net;
using System.Security.Cryptography;

using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Remote.Contracts;
using Microsoft.Extensions.Logging;

namespace FFApp.Remote;

/// <summary>
/// Runs one queue item on the server agent: upload (resumable, SHA-256 computed while sending),
/// submit, wait for the agent, then download and acknowledge, or leave the result on the server.
/// Progress is persisted in <see cref="RemoteJobStore"/>, so a restarted app continues where it
/// stopped. The encode itself never depends on this computer.
/// </summary>
public sealed class RemoteJobExecutor(
    RemoteAgentConnection connection,
    RemoteJobStore store,
    ISleepInhibitor sleepInhibitor,
    TimeProvider timeProvider,
    ILogger<RemoteJobExecutor> logger)
{
    private static readonly long UploadChunkBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BestEffortTimeout = TimeSpan.FromSeconds(10);
    private static readonly int CopyBufferSize = 1024 * 1024;
    private static readonly string PartialSuffix = ".ffapp-download";
    private static readonly string TransferReason = "FFApp is transferring files to the server agent";

    private readonly CancellationTokenSource _detach = new();

    /// <summary>
    /// Stops following server jobs without canceling them, for app shutdown. They continue on
    /// the server and the next app start picks them up from the store.
    /// </summary>
    public void Detach() => _detach.Cancel();

    public bool IsDetached => _detach.IsCancellationRequested;

    /// <summary>Server jobs from a previous session that should continue.</summary>
    public IReadOnlyList<RemoteJobRecord> RestorableJobs() => [.. store.All().Where(record => !record.Finished)];

    /// <summary>
    /// Removes jobs that failed or were canceled in a previous session from the agent, so their
    /// sources do not wait for the retention period.
    /// </summary>
    public async Task CleanUpFinishedAsync(CancellationToken ct)
    {
        foreach (var record in store.All().Where(record => record.Finished))
            await ForgetAsync(record.Key, ct);
    }

    /// <summary>Called when the user removes a queue item: deletes its server job, if any.</summary>
    public async Task ForgetAsync(string key, CancellationToken ct)
    {
        if (store.Find(key) is not { } record)
            return;
        store.Remove(key);
        foreach (var jobId in new[] { record.AgentJobId, record.PreviousAgentJobId })
        {
            if (jobId is not null && connection.Endpoint is { } endpoint)
                await BestEffortAsync(() => connection.Client.DeleteJobAsync(endpoint, jobId, ct), "remove the server job");
        }
    }

    public async Task<JobResult> ExecuteAsync(
        RemoteTarget target,
        string displayLabel,
        JobDefinition job,
        SourceMediaInfo media,
        Action<JobProgress>? progress,
        Action<string>? log,
        ExecutionPauseState? pause,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _detach.Token);
        var token = linked.Token;
        var context = new Context(target, progress, log, pause, timeProvider);

        var record = store.Find(target.Key) ?? new RemoteJobRecord(target.Key, displayLabel, job, media,
            target.ResultMode, target.ServerOutputRoot, RemotePhase.Uploading, timeProvider.GetUtcNow());
        if (record.AgentJobId is null)
            record = record with { Job = job, MediaInfo = media, ResultMode = target.ResultMode, ServerOutputRoot = target.ServerOutputRoot };
        record = record with { Finished = false };
        store.Save(record);

        try
        {
            var endpoint = connection.RequireEndpoint();
            record = await ReuseOrResetAsync(endpoint, record, context, token);

            if (record.AgentJobId is null)
            {
                record = await UploadAndSubmitAsync(endpoint, record, context, token);
                if (record.PreviousAgentJobId is { } previous)
                {
                    await BestEffortAsync(() => connection.Client.DeleteJobAsync(endpoint, previous, token), "remove the previous attempt");
                    record = record with { PreviousAgentJobId = null };
                    store.Save(record);
                }
            }

            context.Report(new RemoteProgress(RemotePhase.WaitingForServer));
            var final = await WaitForAgentAsync(record, context, token);
            return await FinishAsync(connection.RequireEndpoint(), record, final, context, token);
        }
        catch (OperationCanceledException) when (_detach.IsCancellationRequested)
        {
            context.Log("[Server] The app is closing; the job continues on the server.");
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (store.Find(target.Key)?.AgentJobId is { } jobId && connection.Endpoint is { } endpoint)
            {
                context.Log("[Server] Canceling the job on the server…");
                await BestEffortAsync(() => connection.Client.CancelJobAsync(endpoint, jobId, CancellationToken.None), "cancel the server job");
            }
            MarkFinished(target.Key);
            throw;
        }
        catch (RemoteAgentException ex)
        {
            MarkFinished(target.Key);
            context.Log($"[Server] {ex.Message}");
            return Failed(job, ex.Message, context);
        }
    }

    // --- Phases ---

    /// <summary>
    /// Decides how to continue a job that already reached the agent: wait for it, download it,
    /// or start over after a failure ("Run again" reuses the uploaded source).
    /// </summary>
    private async Task<RemoteJobRecord> ReuseOrResetAsync(AgentEndpoint endpoint, RemoteJobRecord record, Context context, CancellationToken ct)
    {
        if (record.AgentJobId is not { } jobId)
            return record;

        // Already on the server: the upload lane is free for the next job.
        context.Report(new RemoteProgress(RemotePhase.WaitingForServer));
        AgentJob? existing;
        try
        {
            existing = await RetryAsync(() => connection.Client.GetJobAsync(endpoint, jobId, ct), context, ct);
        }
        catch (RemoteAgentException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            existing = null;
        }

        if (existing is null)
        {
            context.Log("[Server] The previous server job no longer exists; submitting again.");
            record = record with { AgentJobId = null, ClientJobId = null, Phase = RemotePhase.Uploading };
        }
        else if (existing.State is AgentJobState.Failed or AgentJobState.Canceled)
        {
            record = record with { PreviousAgentJobId = jobId, AgentJobId = null, ClientJobId = null, Phase = RemotePhase.Uploading };
        }
        store.Save(record);
        return record;
    }

    private async Task<RemoteJobRecord> UploadAndSubmitAsync(AgentEndpoint endpoint, RemoteJobRecord record, Context context, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var sourceId = await UploadAsync(endpoint, record, context, ct);
            record = record with { SourceId = sourceId, ClientJobId = record.ClientJobId ?? Guid.NewGuid().ToString("N") };
            store.Save(record);

            var request = new CreateJobRequest(record.ClientJobId!, sourceId, record.Job, record.MediaInfo, record.ResultMode,
                Path.GetFileName(record.Job.OutputPath), record.ServerOutputRoot);
            try
            {
                var created = await RetryAsync(() => connection.Client.CreateJobAsync(endpoint, request, ct), context, ct);
                record = record with { AgentJobId = created.JobId, Phase = RemotePhase.WaitingForServer };
                store.Save(record);
                context.Log($"[Server] Submitted as job {created.JobId}.");
                return record;
            }
            catch (RemoteAgentException ex) when (ex.StatusCode == HttpStatusCode.NotFound && attempt == 1)
            {
                // The source was released between upload and submit (its last job finished); upload it again.
                context.Log("[Server] The uploaded source is gone; uploading again.");
                record = record with { SourceId = null };
            }
        }
    }

    private async Task<string> UploadAsync(AgentEndpoint endpoint, RemoteJobRecord record, Context context, CancellationToken ct)
    {
        var path = record.Job.SourcePath;
        context.Report(new RemoteProgress(RemotePhase.Uploading, 0, Notice: "Checking the server…"));
        var fingerprint = await SourceFingerprint.ComputeAsync(path, ct);
        var size = new FileInfo(path).Length;
        var registration = new CreateSourceRequest(fingerprint, size, Path.GetFileName(path));

        for (var attempt = 1; ; attempt++)
        {
            var status = await RetryAsync(() => connection.Client.RegisterSourceAsync(endpoint, registration, ct), context, ct);
            if (status.Complete)
            {
                context.Log("[Server] The source is already on the server; skipping the upload.");
                return status.SourceId;
            }

            using (sleepInhibitor.Acquire(TransferReason))
            {
                var sha256 = await SendAsync(endpoint, path, registration, status, context, ct);
                try
                {
                    context.Report(new RemoteProgress(RemotePhase.Uploading, 100, Notice: "Verifying the upload…"));
                    await RetryAsync(() => connection.Client.CompleteSourceAsync(endpoint, status.SourceId, sha256, ct), context, ct);
                    context.Log($"[Server] Uploaded {FormatBytes(size)}.");
                    return status.SourceId;
                }
                catch (RemoteAgentException ex) when (ex.Code == ErrorCodes.HashMismatch && attempt == 1)
                {
                    context.Log("[Server] The upload arrived damaged; uploading it once more.");
                }
            }
        }
    }

    /// <summary>
    /// Sends the file from the offset the agent confirmed. After a dropped connection it asks the
    /// agent again for the confirmed offset and continues there. Returns the file's SHA-256.
    /// </summary>
    private async Task<string> SendAsync(
        AgentEndpoint endpoint, string path, CreateSourceRequest registration, SourceStatus status, Context context, CancellationToken ct)
    {
        var size = status.Size;
        var rate = new TransferRate(timeProvider);
        var backoff = MinimumBackoff;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
        if (file.Length != size)
            throw new RemoteAgentException("The source file changed since it was queued. Add it to the queue again.");

        while (true)
        {
            var confirmed = status.ReceivedBytes;
            using var hash = await HashPrefixAsync(file, confirmed, context, ct);
            try
            {
                while (confirmed < size)
                {
                    if (context.Pause is { } pause)
                        await pause.WaitWhilePausedAsync(ct);

                    var length = Math.Min(UploadChunkBytes, size - confirmed);
                    file.Seek(confirmed, SeekOrigin.Begin);
                    var sent = 0L;
                    var start = confirmed;
                    var window = new HashingWindowStream(file, length, hash, read =>
                    {
                        sent += read;
                        rate.Add(read);
                        context.ReportThrottled(new RemoteProgress(RemotePhase.Uploading,
                            100.0 * (start + sent) / size, rate.BytesPerSecond));
                    });
                    status = await connection.Client.UploadChunkAsync(endpoint, status.SourceId, window, confirmed, length, size, ct);
                    if (status.ReceivedBytes != confirmed + length)
                        throw new RemoteAgentException("The server agent confirmed an unexpected upload offset.", code: ErrorCodes.OffsetMismatch);
                    confirmed = status.ReceivedBytes;
                    backoff = MinimumBackoff;
                }
                return Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            catch (RemoteAgentException ex) when (ex.IsTransient || ex.Code == ErrorCodes.OffsetMismatch)
            {
                context.Report(new RemoteProgress(RemotePhase.Uploading, 100.0 * confirmed / size, Notice: "Connection lost; resuming…"));
                logger.LogInformation("Upload interrupted: {Message}", ex.Message);
                await Task.Delay(backoff, timeProvider, ct);
                backoff = Next(backoff);
                // Ask the agent what it kept; the hash restarts from that offset.
                status = await RetryAsync(() => connection.Client.RegisterSourceAsync(endpoint, registration, ct), context, ct);
                if (status.Complete)
                    throw new RemoteAgentException("The server agent reports the upload as complete before it was verified.");
            }
        }
    }

    /// <summary>Feeds the part the agent already has into the hash, so resuming still yields the whole file's hash.</summary>
    private static async Task<IncrementalHash> HashPrefixAsync(FileStream file, long length, Context context, CancellationToken ct)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (length == 0)
            return hash;
        context.Report(new RemoteProgress(RemotePhase.Uploading, null, Notice: "Preparing to resume the upload…"));
        file.Seek(0, SeekOrigin.Begin);
        var buffer = new byte[CopyBufferSize];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0)
                throw new IOException("The source file became shorter during the upload.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return hash;
    }

    /// <summary>Follows the agent job through events, with polling as a safety net, until it finishes.</summary>
    private async Task<AgentJob> WaitForAgentAsync(RemoteJobRecord record, Context context, CancellationToken ct)
    {
        var jobId = record.AgentJobId ?? throw new InvalidOperationException("The job was not submitted.");
        var finished = new TaskCompletionSource<AgentJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wake = new SemaphoreSlim(0);
        AgentJob? latest = null;
        var stateLock = new Lock();

        void OnJob(AgentJob job)
        {
            if (!string.Equals(job.JobId, jobId, StringComparison.Ordinal))
                return;
            lock (stateLock)
                latest = job;
            Apply(job);
        }

        void Apply(AgentJob job)
        {
            if (job.IsFinished)
            {
                finished.TrySetResult(job);
                return;
            }
            if (job.State == AgentJobState.Running && context.Pause?.IsPaused == true && connection.Endpoint is { } endpoint)
            {
                // Paused here before the agent started it: hold it there too.
                FireAndForget(() => connection.Client.PauseJobAsync(endpoint, jobId, CancellationToken.None), "pause the server job");
            }
            context.Report(job.State == AgentJobState.Queued
                ? new RemoteProgress(RemotePhase.WaitingForServer, QueuePosition: job.QueuePosition, EstimatedStartAt: job.EstimatedStartAt)
                : new RemoteProgress(RemotePhase.Running, IsPaused: job.State == AgentJobState.Paused), job.Progress);
        }

        void OnProgress(JobProgressEvent update)
        {
            if (!string.Equals(update.JobId, jobId, StringComparison.Ordinal))
                return;
            bool paused;
            lock (stateLock)
                paused = latest?.State == AgentJobState.Paused;
            context.Report(new RemoteProgress(RemotePhase.Running, IsPaused: paused), update.Progress);
        }

        void OnLog(JobLogEvent line)
        {
            if (string.Equals(line.JobId, jobId, StringComparison.Ordinal))
                context.Log(line.Line);
        }

        void OnConnected() => wake.Release();

        connection.JobChanged += OnJob;
        connection.JobProgressReceived += OnProgress;
        connection.JobLogReceived += OnLog;
        connection.Connected += OnConnected;
        context.Pause?.StartProcess(
            () => { },
            () => FireAndForgetControl(jobId, pause: true),
            () => FireAndForgetControl(jobId, pause: false));
        try
        {
            while (!finished.Task.IsCompleted)
            {
                try
                {
                    var endpoint = connection.RequireEndpoint();
                    var job = await connection.Client.GetJobAsync(endpoint, jobId, ct);
                    lock (stateLock)
                        latest = job;
                    Apply(job);
                }
                catch (RemoteAgentException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new RemoteAgentException("The job disappeared from the server agent.", ex.StatusCode, ex.Code, ex);
                }
                catch (RemoteAgentException ex) when (ex.IsTransient)
                {
                    context.Report(new RemoteProgress(RemotePhase.WaitingForServer,
                        Notice: "Server agent not reachable; the job continues there."));
                    logger.LogInformation("Waiting for the server agent: {Message}", ex.Message);
                }

                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(PollInterval);
                try
                {
                    await Task.WhenAny(finished.Task, wake.WaitAsync(waitCts.Token)).WaitAsync(ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Poll interval elapsed.
                }
            }
            return await finished.Task;
        }
        finally
        {
            context.Pause?.DetachProcess();
            connection.JobChanged -= OnJob;
            connection.JobProgressReceived -= OnProgress;
            connection.JobLogReceived -= OnLog;
            connection.Connected -= OnConnected;
        }
    }

    private async Task<JobResult> FinishAsync(AgentEndpoint endpoint, RemoteJobRecord record, AgentJob final, Context context, CancellationToken ct)
    {
        var elapsed = final.Progress?.ElapsedSeconds ?? 0;
        switch (final.State)
        {
            case AgentJobState.Completed when record.ResultMode == ResultMode.Download:
            {
                record = record with { Phase = RemotePhase.Downloading };
                store.Save(record);
                var output = await DownloadAsync(endpoint, record, final, context, ct);
                await RetryAsync(async () =>
                {
                    await connection.Client.AcknowledgeAsync(endpoint, final.JobId, ct);
                    return true;
                }, context, ct);
                store.Remove(record.Key);
                context.Report(new RemoteProgress(RemotePhase.Done, 100));
                return new JobResult(record.Job with { OutputPath = output }, JobState.Completed, [], [], elapsed, "");
            }
            case AgentJobState.Completed:
                context.Log($"[Server] Saved on the server: {final.ServerOutputPath}");
                await BestEffortAsync(() => connection.Client.DeleteJobAsync(endpoint, final.JobId, ct), "remove the finished server job");
                store.Remove(record.Key);
                context.Report(new RemoteProgress(RemotePhase.Done, 100));
                return new JobResult(record.Job, JobState.Completed, [], [], elapsed, "");
            case AgentJobState.Canceled:
                MarkFinished(record.Key);
                return new JobResult(record.Job, JobState.Canceled, [], [], elapsed, "");
            default:
                MarkFinished(record.Key);
                var log = "";
                await BestEffortAsync(async () => log = await connection.Client.GetLogAsync(endpoint, final.JobId, ct), "read the server log");
                return Failed(record.Job, final.FailureMessage ?? "The job failed on the server.", context, log, elapsed);
        }
    }

    /// <summary>
    /// Downloads to a partial file next to the target, resuming with <c>Range</c>, then moves it
    /// into place without overwriting an existing file.
    /// </summary>
    private async Task<string> DownloadAsync(AgentEndpoint endpoint, RemoteJobRecord record, AgentJob job, Context context, CancellationToken ct)
    {
        var target = record.Job.OutputPath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(target)) ?? ".";
        Directory.CreateDirectory(directory);
        var partial = target + PartialSuffix;
        var total = job.OutputSizeBytes ?? 0;
        var rate = new TransferRate(timeProvider);
        var backoff = MinimumBackoff;

        using (sleepInhibitor.Acquire(TransferReason))
        {
            while (true)
            {
                if (context.Pause is { } pause)
                    await pause.WaitWhilePausedAsync(ct);
                var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (total > 0 && offset >= total)
                    break;
                try
                {
                    using var response = await connection.Client.OpenOutputAsync(endpoint, job.JobId, offset, ct);
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                        offset = 0;
                    total = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength ?? total;
                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    await using var file = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append,
                        FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
                    var buffer = new byte[CopyBufferSize];
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                        offset += read;
                        rate.Add(read);
                        context.ReportThrottled(new RemoteProgress(RemotePhase.Downloading,
                            total > 0 ? 100.0 * offset / total : null, rate.BytesPerSecond));
                        if (context.Pause?.IsPaused == true)
                            break;
                    }
                    if (context.Pause?.IsPaused == true)
                        continue;
                    if (total == 0 || offset >= total)
                        break;
                }
                catch (IOException ex) when (IsDiskFull(ex))
                {
                    throw new RemoteAgentException(
                        $"There is not enough space to download the result ({FormatBytes(total)}). It stays on the server; free some space and run the job again to download it.",
                        inner: ex);
                }
                catch (Exception ex) when (ex is RemoteAgentException { IsTransient: true } or IOException or HttpRequestException)
                {
                    context.Report(new RemoteProgress(RemotePhase.Downloading, total > 0 ? 100.0 * offset / total : null,
                        Notice: "Connection lost; resuming…"));
                    logger.LogInformation("Download interrupted: {Message}", ex.Message);
                    await Task.Delay(backoff, timeProvider, ct);
                    backoff = Next(backoff);
                }
            }
        }

        var final = File.Exists(target)
            ? DefaultCommandBuilder.SuggestOutputPath(target, record.Job.Container, directory,
                Path.GetFileNameWithoutExtension(target), isAutoName: true)
            : target;
        File.Move(partial, final);
        if (!string.Equals(final, target, StringComparison.Ordinal))
            context.Log($"[Server] {Path.GetFileName(target)} already exists; saved the result as {Path.GetFileName(final)}.");
        context.Log($"[Server] Downloaded {FormatBytes(total)}.");
        return final;
    }

    // --- Helpers ---

    /// <summary>Retries transient failures with backoff until they succeed or the job is canceled.</summary>
    private async Task<T> RetryAsync<T>(Func<Task<T>> action, Context context, CancellationToken ct)
    {
        var backoff = MinimumBackoff;
        while (true)
        {
            try
            {
                return await action();
            }
            catch (RemoteAgentException ex) when (ex.IsTransient && !ct.IsCancellationRequested)
            {
                context.ReportNotice("Server agent not reachable; retrying…");
                logger.LogInformation("Retrying after: {Message}", ex.Message);
                await Task.Delay(backoff, timeProvider, ct);
                backoff = Next(backoff);
            }
        }
    }

    private static TimeSpan Next(TimeSpan backoff) => TimeSpan.FromTicks(Math.Min(MaximumBackoff.Ticks, backoff.Ticks * 2));

    private void MarkFinished(string key)
    {
        if (store.Find(key) is { } record)
            store.Save(record with { Finished = true });
    }

    private static JobResult Failed(JobDefinition job, string message, Context context, string log = "", double elapsed = 0)
    {
        context.Log($"[Server] {message}");
        var output = string.IsNullOrWhiteSpace(log) ? message : log + Environment.NewLine + message;
        return new JobResult(job, JobState.Failed, [], [], elapsed, output);
    }

    private void FireAndForgetControl(string jobId, bool pause)
    {
        if (connection.Endpoint is not { } endpoint)
            return;
        FireAndForget(pause
            ? () => connection.Client.PauseJobAsync(endpoint, jobId, CancellationToken.None)
            : () => connection.Client.ResumeJobAsync(endpoint, jobId, CancellationToken.None),
            pause ? "pause the server job" : "resume the server job");
    }

    /// <summary>
    /// <see cref="ExecutionPauseState"/> calls pause and resume synchronously under its lock, so the
    /// HTTP request cannot be awaited there. The task is observed and failures are logged.
    /// </summary>
    private void FireAndForget(Func<Task> action, string what) =>
        _ = Task.Run(() => BestEffortAsync(action, what));

    private async Task BestEffortAsync(Func<Task> action, string what)
    {
        try
        {
            using var timeout = new CancellationTokenSource(BestEffortTimeout);
            await action().WaitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is RemoteAgentException or OperationCanceledException or TimeoutException)
        {
            logger.LogWarning("Could not {What}: {Message}", what, ex.Message);
        }
    }

    private static bool IsDiskFull(IOException ex) =>
        ex.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) or 28
        || ex.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase);

    private static string FormatBytes(long bytes) => bytes >= 1_000_000_000
        ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{bytes / 1_000_000_000.0:0.0} GB")
        : string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{bytes / 1_000_000.0:0.0} MB");

    /// <summary>Per-execution reporting with throttling of transfer progress.</summary>
    private sealed class Context(
        RemoteTarget target,
        Action<JobProgress>? progress,
        Action<string>? log,
        ExecutionPauseState? pause,
        TimeProvider timeProvider)
    {
        private readonly Lock _lock = new();
        private long _lastReport = long.MinValue;
        private RemoteProgress _last = new(RemotePhase.Uploading);
        private JobProgress? _lastEncode;

        public RemoteTarget Target => target;
        public ExecutionPauseState? Pause => pause;

        public void Log(string line) => log?.Invoke(line);

        public void Report(RemoteProgress remote, JobProgress? encode = null)
        {
            lock (_lock)
            {
                _last = remote;
                if (encode is not null)
                    _lastEncode = encode;
                _lastReport = timeProvider.GetTimestamp();
                var baseline = remote.Phase == RemotePhase.Running && _lastEncode is { } running
                    ? running with { State = JobState.Running }
                    : new JobProgress(JobState.Running, 1, 1, _lastEncode?.ElapsedSeconds ?? 0, Percentage: remote.Percentage);
                progress?.Invoke(baseline with { Remote = remote with { IsPaused = remote.IsPaused || pause?.IsPaused == true } });
            }
        }

        public void ReportThrottled(RemoteProgress remote)
        {
            lock (_lock)
            {
                if (_lastReport != long.MinValue && timeProvider.GetElapsedTime(_lastReport) < ReportInterval)
                    return;
            }
            Report(remote);
        }

        public void ReportNotice(string notice)
        {
            RemoteProgress last;
            lock (_lock)
                last = _last;
            Report(last with { Notice = notice });
        }
    }
}
