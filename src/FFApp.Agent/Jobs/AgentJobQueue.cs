using System.Collections.Immutable;
using System.Text.Json;

using FFApp.Agent.Events;
using FFApp.Agent.Storage;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;
using FFApp.Infrastructure;
using FFApp.Remote.Contracts;
using Microsoft.Extensions.Options;

namespace FFApp.Agent.Jobs;

/// <summary>Cancellation and pause handles of a job that is being executed.</summary>
public sealed class ActiveExecution(TimeProvider timeProvider) : IDisposable
{
    public CancellationTokenSource Cancellation { get; } = new();
    public ExecutionPauseState Pause { get; } = new(timeProvider);

    public void Dispose() => Cancellation.Dispose();
}

/// <summary>
/// All jobs of all clients, persisted one JSON file per job. Jobs start strictly first in, first
/// out. A client only ever sees and controls its own jobs; other clients' jobs appear only as
/// anonymous load information.
/// </summary>
public sealed class AgentJobQueue(
    AgentPaths paths,
    SourceStore sources,
    ThroughputHistory throughput,
    EventHub events,
    ICommandBuilder commandBuilder,
    IFfmpegCapabilityService capabilities,
    IOptions<AgentOptions> options,
    TimeProvider timeProvider,
    ILogger<AgentJobQueue> logger)
{
    private static readonly TimeSpan ProgressEventInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan QueueEventInterval = TimeSpan.FromSeconds(5);
    public static readonly string InterruptedMessage = "The agent restarted while the job was running. Run it again; the source is still on the server.";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, AgentJobRecord> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveExecution> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastProgressEvent = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wakeup = new(0);
    private long _lastQueueEvent;
    private long _sequence;
    private bool _startsBlocked;

    /// <summary>Loads persisted jobs. Jobs that were running when the agent stopped become failed.</summary>
    public void Load()
    {
        paths.EnsureCreated();
        lock (_lock)
        {
            _jobs.Clear();
            foreach (var directory in Directory.EnumerateDirectories(paths.Jobs))
            {
                var id = Path.GetFileName(directory);
                AgentJobRecord? record;
                try
                {
                    record = AtomicFile.ReadJson(paths.JobFile(id), AgentJsonContext.Default.AgentJobRecord);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    logger.LogError(ex, "Skipping unreadable job {JobId}", id);
                    continue;
                }
                if (record is null)
                {
                    TryDeleteDirectory(directory);
                    continue;
                }

                if (record.IsActive)
                {
                    TryDeleteFile(record.OutputPath);
                    record = record with
                    {
                        State = AgentJobState.Failed,
                        FailureMessage = InterruptedMessage,
                        FinishedAt = timeProvider.GetUtcNow(),
                        OutputPath = null,
                    };
                    AtomicFile.WriteJson(paths.JobFile(id), record, AgentJsonContext.Default.AgentJobRecord);
                    logger.LogWarning("Job {JobId} failed because the agent restarted", id);
                }
                TryDeleteDirectory(paths.JobWork(id));
                _jobs[id] = record;
                _sequence = Math.Max(_sequence, record.Sequence);
            }
        }
        _wakeup.Release();
    }

    // --- Client operations ---

    public AgentJob Create(string clientId, CreateJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ClientJobId))
            throw AgentException.Invalid("clientJobId is required.");

        lock (_lock)
        {
            var existing = _jobs.Values.FirstOrDefault(job =>
                string.Equals(job.ClientId, clientId, StringComparison.Ordinal)
                && string.Equals(job.ClientJobId, request.ClientJobId, StringComparison.Ordinal));
            if (existing is not null)
                return ToDto(existing, Schedule());
        }

        var source = sources.Get(clientId, request.SourceId);
        if (!source.Complete)
            throw AgentException.Conflict(ErrorCodes.SourceIncomplete, "The source upload is not complete.");
        ValidateRequest(request);

        var jobId = Guid.NewGuid().ToString("N");
        var plannedOutput = Path.Combine(paths.JobDirectory(jobId), "output" + DefaultCommandBuilder.ContainerExtension(request.Job.Container));
        var (job, media) = Rewrite(request.Job, request.MediaInfo, sources.DataPath(source), plannedOutput);
        int passCount;
        try
        {
            if (capabilities.Capabilities is { } available)
            {
                var missing = FfmpegFeatureValidation.Validate(job, media, available);
                if (missing.Count > 0)
                    throw new CommandBuildException(string.Join(Environment.NewLine, missing.Select(issue => issue.Message)));
            }
            passCount = Math.Max(1, commandBuilder.Build(job, media, paths.JobWork(jobId))
                .Count(command => command.Protocol == ProcessProtocol.Ffmpeg));
        }
        catch (CommandBuildException ex)
        {
            throw new AgentException(StatusCodes.Status422UnprocessableEntity, ErrorCodes.UnsupportedJob, ex.Message);
        }

        lock (_lock)
        {
            var record = new AgentJobRecord(
                jobId, clientId, request.ClientJobId, source.SourceId, request.Job, request.MediaInfo,
                request.ResultMode, request.OutputFileName, request.ServerOutputRoot, AgentJobState.Queued,
                timeProvider.GetUtcNow(), ++_sequence,
                DefaultJobExecutionService.EffectiveOutputDuration(job, media), passCount, ThroughputKind(request.Job));
            Directory.CreateDirectory(paths.JobDirectory(jobId));
            Save(record);
            sources.Touch(source);
            logger.LogInformation("Queued job {JobId} ({ClientJobId}) for client {ClientId}", jobId, request.ClientJobId, clientId);
        }

        PublishJobState(jobId);
        PublishQueueState(force: true);
        _wakeup.Release();
        return Get(clientId, jobId);
    }

    public IReadOnlyList<AgentJob> List(string clientId)
    {
        lock (_lock)
        {
            var schedule = Schedule();
            return [.. _jobs.Values
                .Where(job => string.Equals(job.ClientId, clientId, StringComparison.Ordinal))
                .OrderBy(job => job.Sequence)
                .Select(job => ToDto(job, schedule))];
        }
    }

    public AgentJob Get(string clientId, string jobId)
    {
        lock (_lock)
            return ToDto(Own(clientId, jobId), Schedule());
    }

    public AgentJobRecord GetRecord(string clientId, string jobId)
    {
        lock (_lock)
            return Own(clientId, jobId);
    }

    public AgentJob Cancel(string clientId, string jobId)
    {
        lock (_lock)
        {
            var job = Own(clientId, jobId);
            switch (job.State)
            {
                case AgentJobState.Queued:
                    Save(job with { State = AgentJobState.Canceled, FinishedAt = timeProvider.GetUtcNow() });
                    break;
                case AgentJobState.Running or AgentJobState.Paused:
                    if (_active.TryGetValue(jobId, out var execution))
                    {
                        execution.Pause.Resume();
                        execution.Cancellation.Cancel();
                    }
                    break;
                default:
                    throw AgentException.Conflict(ErrorCodes.InvalidState, "Only waiting or running jobs can be canceled.");
            }
        }
        PublishJobState(jobId);
        PublishQueueState(force: true);
        return Get(clientId, jobId);
    }

    public AgentJob Pause(string clientId, string jobId)
    {
        lock (_lock)
        {
            var job = Own(clientId, jobId);
            if (job.State == AgentJobState.Paused)
                return ToDto(job, Schedule());
            if (job.State != AgentJobState.Running || !_active.TryGetValue(jobId, out var execution))
                throw AgentException.Conflict(ErrorCodes.InvalidState, "Only a running job can be paused.");
            execution.Pause.Pause();
            Save(job with { State = AgentJobState.Paused });
        }
        PublishJobState(jobId);
        PublishQueueState(force: true);
        return Get(clientId, jobId);
    }

    public AgentJob Resume(string clientId, string jobId)
    {
        lock (_lock)
        {
            var job = Own(clientId, jobId);
            if (job.State == AgentJobState.Running)
                return ToDto(job, Schedule());
            if (job.State != AgentJobState.Paused || !_active.TryGetValue(jobId, out var execution))
                throw AgentException.Conflict(ErrorCodes.InvalidState, "Only a paused job can be resumed.");
            execution.Pause.Resume();
            Save(job with { State = AgentJobState.Running });
        }
        PublishJobState(jobId);
        PublishQueueState(force: true);
        return Get(clientId, jobId);
    }

    /// <summary>Path of a completed result that waits for download.</summary>
    public string OutputForDownload(string clientId, string jobId)
    {
        lock (_lock)
        {
            var job = Own(clientId, jobId);
            if (job.State != AgentJobState.Completed || job.ResultMode != ResultMode.Download
                || job.OutputPath is not { } path || !File.Exists(path))
            {
                throw AgentException.Conflict(ErrorCodes.InvalidState, "This job has no result to download.");
            }
            return path;
        }
    }

    /// <summary>The client has the result: delete it and release the source when no job needs it.</summary>
    public void Acknowledge(string clientId, string jobId)
    {
        lock (_lock)
        {
            var job = Own(clientId, jobId);
            if (job.State != AgentJobState.Completed || job.ResultMode != ResultMode.Download)
                throw AgentException.Conflict(ErrorCodes.InvalidState, "Only a completed download job can be acknowledged.");
            RemoveJob(job);
        }
        PublishQueueState(force: true);
    }

    /// <summary>Removes a job that is not running. A result kept on the server stays in its folder.</summary>
    public void Delete(string clientId, string jobId)
    {
        lock (_lock)
        {
            var job = Own(clientId, jobId);
            if (job.IsActive)
                throw AgentException.Conflict(ErrorCodes.InvalidState, "Cancel the running job before removing it.");
            RemoveJob(job);
        }
        PublishQueueState(force: true);
    }

    public QueueState QueueStateFor(string clientId)
    {
        lock (_lock)
        {
            var schedule = Schedule();
            var now = timeProvider.GetUtcNow();
            var running = _jobs.Values.FirstOrDefault(job => job.IsActive);
            var waiting = Waiting();
            return new QueueState(
                running is null ? null : new RunningJobInfo(
                    string.Equals(running.ClientId, clientId, StringComparison.Ordinal),
                    OverallPercent(running),
                    schedule.RunningRemainingSeconds is { } remaining ? now.AddSeconds(remaining) : null),
                waiting.Count,
                waiting.Count(job => string.Equals(job.ClientId, clientId, StringComparison.Ordinal)),
                schedule.IdleAfterSeconds is { } idle ? now.AddSeconds(idle) : null);
        }
    }

    // --- Worker operations ---

    /// <summary>When set, waiting jobs are accepted but none is started (used before an update).</summary>
    public bool StartsBlocked
    {
        get { lock (_lock) return _startsBlocked; }
        set
        {
            lock (_lock)
                _startsBlocked = value;
            _wakeup.Release();
        }
    }

    public bool HasActiveJobs
    {
        get { lock (_lock) return _active.Count > 0; }
    }

    public long OutputBytes()
    {
        lock (_lock)
            return _jobs.Values.Where(job => job.ResultMode == ResultMode.Download).Sum(job => job.OutputSizeBytes ?? 0);
    }

    /// <summary>Waits for the next job and marks it running.</summary>
    public async Task<(AgentJobRecord Job, ActiveExecution Execution)> WaitForNextAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_lock)
            {
                if (!_startsBlocked && _active.Count == 0 && Waiting().FirstOrDefault() is { } next)
                {
                    var execution = new ActiveExecution(timeProvider);
                    _active[next.JobId] = execution;
                    var running = next with { State = AgentJobState.Running, StartedAt = timeProvider.GetUtcNow() };
                    Save(running);
                    PublishJobStateLocked(running);
                    PublishQueueStateLocked(force: true);
                    return (running, execution);
                }
            }
            await _wakeup.WaitAsync(ct);
        }
    }

    public (JobDefinition Job, SourceMediaInfo Media) PrepareExecution(AgentJobRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var source = sources.All().FirstOrDefault(s => string.Equals(s.SourceId, record.SourceId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The job's source is no longer on the server.");
        string output;
        if (record.ResultMode == ResultMode.KeepOnServer)
        {
            var root = options.Value.OutputRoots.GetValueOrDefault(record.ServerOutputRoot ?? "")
                ?? throw new InvalidOperationException($"The output folder \"{record.ServerOutputRoot}\" is no longer configured.");
            Directory.CreateDirectory(root);
            output = DefaultCommandBuilder.SuggestOutputPath(record.OutputFileName, record.Job.Container, root,
                Path.GetFileNameWithoutExtension(record.OutputFileName), isAutoName: true);
        }
        else
        {
            output = Path.Combine(paths.JobDirectory(record.JobId), "output" + DefaultCommandBuilder.ContainerExtension(record.Job.Container));
        }

        lock (_lock)
        {
            if (_jobs.TryGetValue(record.JobId, out var current))
                Save(current with { OutputPath = output });
        }
        return Rewrite(record.Job, record.MediaInfo, sources.DataPath(source), output);
    }

    public void ReportProgress(string jobId, JobProgress progress)
    {
        AgentJobRecord? updated = null;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var job) || !job.IsActive)
                return;
            updated = job with { Progress = progress };
            _jobs[jobId] = updated;

            var now = timeProvider.GetTimestamp();
            if (_lastProgressEvent.TryGetValue(jobId, out var last) && timeProvider.GetElapsedTime(last, now) < ProgressEventInterval)
                updated = null;
            else
                _lastProgressEvent[jobId] = now;
        }
        if (updated is not null)
        {
            events.PublishTo(updated.ClientId, AgentEventTypes.JobProgress,
                JsonSerializer.Serialize(new JobProgressEvent(jobId, progress), RemoteJsonContext.Default.JobProgressEvent));
            PublishQueueState(force: false);
        }
    }

    public void ReportLog(string jobId, string clientId, string line) =>
        events.PublishTo(clientId, AgentEventTypes.JobLog,
            JsonSerializer.Serialize(new JobLogEvent(jobId, line), RemoteJsonContext.Default.JobLogEvent));

    /// <summary>Records the outcome of an execution and releases the encoder for the next job.</summary>
    public void Finish(string jobId, AgentJobState state, string? failureMessage, double activeSeconds)
    {
        lock (_lock)
        {
            if (_active.Remove(jobId, out var execution))
                execution.Dispose();
            _lastProgressEvent.Remove(jobId);
            if (!_jobs.TryGetValue(jobId, out var job))
                return;

            long? size = null;
            if (state == AgentJobState.Completed && job.OutputPath is { } output && File.Exists(output))
                size = new FileInfo(output).Length;
            else
                TryDeleteFile(job.OutputPath);

            var finished = job with
            {
                State = state,
                FinishedAt = timeProvider.GetUtcNow(),
                FailureMessage = failureMessage,
                OutputSizeBytes = size,
                OutputPath = state == AgentJobState.Completed ? job.OutputPath : null,
                Progress = job.Progress is { } progress ? progress with { State = ToJobState(state) } : null,
            };
            Save(finished);
            TryDeleteDirectory(paths.JobWork(jobId));

            if (state == AgentJobState.Completed && finished.MediaSeconds is > 0 and var media && activeSeconds > 0)
                throughput.Add(new ThroughputSample(finished.ThroughputKind, media, finished.PassCount, activeSeconds, timeProvider.GetUtcNow()));
            logger.LogInformation("Job {JobId} finished: {State}", jobId, state);

            PublishJobStateLocked(finished);
            PublishQueueStateLocked(force: true);
        }
        _wakeup.Release();
    }

    // --- Maintenance ---

    /// <summary>Deletes jobs and sources of clients that were removed from <c>clients.json</c>.</summary>
    public void PurgeClients(IReadOnlySet<string> knownClientIds)
    {
        ArgumentNullException.ThrowIfNull(knownClientIds);
        lock (_lock)
        {
            foreach (var job in _jobs.Values.Where(job => !knownClientIds.Contains(job.ClientId)).ToList())
            {
                if (_active.TryGetValue(job.JobId, out var execution))
                {
                    execution.Pause.Resume();
                    execution.Cancellation.Cancel();
                    continue;
                }
                logger.LogInformation("Removing job {JobId} of removed client {ClientId}", job.JobId, job.ClientId);
                DeleteResultForRemoval(job);
                RemoveJob(job);
            }
        }
        foreach (var source in sources.All().Where(source => !knownClientIds.Contains(source.ClientId)))
        {
            lock (_lock)
            {
                if (_jobs.Values.Any(job => string.Equals(job.SourceId, source.SourceId, StringComparison.Ordinal)))
                    continue;
            }
            sources.Delete(source.SourceId);
        }
    }

    /// <summary>Deletes finished jobs and unused sources older than the retention period.</summary>
    public void ApplyRetention()
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-options.Value.RetentionDays);
        lock (_lock)
        {
            foreach (var job in _jobs.Values.Where(job => job.IsFinished && job.FinishedAt < cutoff).ToList())
            {
                logger.LogInformation("Retention: removing job {JobId}", job.JobId);
                RemoveJob(job);
            }
        }
        foreach (var source in sources.All().Where(source => source.LastUsedAt < cutoff))
        {
            lock (_lock)
            {
                if (_jobs.Values.Any(job => string.Equals(job.SourceId, source.SourceId, StringComparison.Ordinal)))
                    continue;
            }
            logger.LogInformation("Retention: removing source {SourceId}", source.SourceId);
            sources.Delete(source.SourceId);
        }
    }

    // --- Helpers ---

    private AgentJobRecord Own(string clientId, string jobId) =>
        _jobs.TryGetValue(jobId, out var job) && string.Equals(job.ClientId, clientId, StringComparison.Ordinal)
            ? job
            : throw AgentException.NotFound("The job");

    private List<AgentJobRecord> Waiting() =>
        [.. _jobs.Values.Where(job => job.State == AgentJobState.Queued).OrderBy(job => job.Sequence)];

    private void Save(AgentJobRecord record)
    {
        _jobs[record.JobId] = record;
        AtomicFile.WriteJson(paths.JobFile(record.JobId), record, AgentJsonContext.Default.AgentJobRecord);
    }

    /// <summary>Removes the job record and its directory, then releases its source if unused.</summary>
    private void RemoveJob(AgentJobRecord job)
    {
        _jobs.Remove(job.JobId);
        TryDeleteDirectory(paths.JobDirectory(job.JobId));
        if (!_jobs.Values.Any(other => string.Equals(other.SourceId, job.SourceId, StringComparison.Ordinal)))
            sources.Delete(job.SourceId);
    }

    private void DeleteResultForRemoval(AgentJobRecord job)
    {
        // Results kept on the server belong to the server's media library; only downloads are ours.
        if (job.ResultMode == ResultMode.Download)
            TryDeleteFile(job.OutputPath);
    }

    private QueueSchedule Schedule()
    {
        var running = _jobs.Values.FirstOrDefault(job => job.IsActive);
        var waiting = Waiting();
        return QueueTimeEstimator.EstimateSchedule(
            running is null ? null : EstimateEntry(running),
            [.. waiting.Select(EstimateEntry)],
            throughput.Entries());
    }

    private static QueueEstimateEntry EstimateEntry(AgentJobRecord job) =>
        new(job.IsActive ? JobState.Running : JobState.Pending, job.MediaSeconds, job.PassCount, job.Progress, Kind: job.ThroughputKind);

    private AgentJob ToDto(AgentJobRecord job, QueueSchedule schedule)
    {
        int? position = null, ahead = null;
        DateTimeOffset? start = null;
        if (job.State == AgentJobState.Queued)
        {
            var waiting = Waiting();
            var index = waiting.FindIndex(other => string.Equals(other.JobId, job.JobId, StringComparison.Ordinal));
            if (index >= 0)
            {
                position = index + 1;
                ahead = index + (_jobs.Values.Any(other => other.IsActive) ? 1 : 0);
                if (index < schedule.PendingStartSeconds.Count && schedule.PendingStartSeconds[index] is { } seconds)
                    start = timeProvider.GetUtcNow().AddSeconds(seconds);
            }
        }

        return new AgentJob(
            job.JobId, job.ClientJobId, job.SourceId, job.State, job.ResultMode, job.OutputFileName, job.ServerOutputRoot,
            job.CreatedAt, job.StartedAt, job.FinishedAt, job.Progress, job.FailureMessage, job.OutputSizeBytes,
            job.ResultMode == ResultMode.KeepOnServer && job.State == AgentJobState.Completed ? job.OutputPath : null,
            position, ahead, start);
    }

    private void ValidateRequest(CreateJobRequest request)
    {
        if (request.Job is null || request.MediaInfo is null)
            throw AgentException.Invalid("job and mediaInfo are required.");
        if (!IsBareFileName(request.OutputFileName))
            throw AgentException.Invalid("outputFileName must be a file name without folders.");
        if (!Enum.IsDefined(request.ResultMode))
            throw AgentException.Invalid("Unknown resultMode.");
        if (request.ResultMode == ResultMode.KeepOnServer
            && (request.ServerOutputRoot is null || !options.Value.OutputRoots.ContainsKey(request.ServerOutputRoot)))
        {
            throw AgentException.Invalid($"The output folder \"{request.ServerOutputRoot}\" is not configured on the agent.");
        }

        var job = request.Job;
        if (!Enum.IsDefined(job.Container) || !Enum.IsDefined(job.VideoMode) || !Enum.IsDefined(job.VideoCodec)
            || !Enum.IsDefined(job.EncodingStrategy) || !Enum.IsDefined(job.ResolutionPreset)
            || !Enum.IsDefined(job.FrameRatePreset) || !Enum.IsDefined(job.HdrMode) || !Enum.IsDefined(job.KeyframeUnit)
            || !Enum.IsDefined(job.AudioOptions.Mode) || (job.AudioOptions.Codec is { } codec && !Enum.IsDefined(codec))
            || !Enum.IsDefined(job.ColorMetadata.Mode) || !Enum.IsDefined(job.ToneMapSettings.Engine)
            || !Enum.IsDefined(job.ToneMapSettings.Algorithm) || !Enum.IsDefined(job.ToneMapSettings.LibplaceboAlgorithm)
            || !Enum.IsDefined(job.ToneMapSettings.GamutMode))
        {
            throw AgentException.Invalid("The job contains an unknown option value. Update the agent to the app's version.");
        }
    }

    internal static bool IsBareFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name is not ("." or "..")
        && name.IndexOfAny(['/', '\\']) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Contains("..", StringComparison.Ordinal)
        && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);

    private static (JobDefinition Job, SourceMediaInfo Media) Rewrite(
        JobDefinition job, SourceMediaInfo media, string sourcePath, string outputPath) =>
        (job with { SourcePath = sourcePath, OutputPath = outputPath }, media with { SourcePath = sourcePath });

    /// <summary>Groups jobs whose encoding speed is comparable: same video handling, codec and preset.</summary>
    internal static string ThroughputKind(JobDefinition job)
    {
        if (job.VideoMode != VideoMode.Reencode)
            return job.VideoMode.ToString();
        string preset = job.EncodingStrategy == EncodingStrategy.TargetQuality
            ? job.TargetQuality switch
            {
                H26xQualityOptions quality => quality.Preset,
                Av1QualityOptions quality => quality.Preset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => EncoderPreset(job.VideoEncoderOptions),
            }
            : EncoderPreset(job.VideoEncoderOptions);
        return $"{job.VideoCodec}/{preset}";
    }

    private static string EncoderPreset(VideoEncoderOptions? options) => options switch
    {
        H26xEncoderOptions h26x => h26x.Preset,
        VpxEncoderOptions vpx => vpx.CpuUsed.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Av1EncoderOptions av1 => av1.Preset.ToString(System.Globalization.CultureInfo.InvariantCulture),
        null => "default",
        _ => throw new ArgumentOutOfRangeException(nameof(options), options.GetType().Name, "Unknown encoder options."),
    };

    private static double? OverallPercent(AgentJobRecord job)
    {
        if (job.Progress is not { } progress)
            return null;
        var passes = Math.Max(1, progress.PassCount);
        var fraction = (progress.PassIndex - 1 + (progress.Percentage ?? 0) / 100.0) / passes;
        return Math.Round(Math.Clamp(fraction, 0, 1) * 100, 1);
    }

    private static JobState ToJobState(AgentJobState state) => state switch
    {
        AgentJobState.Queued => JobState.Pending,
        AgentJobState.Running or AgentJobState.Paused => JobState.Running,
        AgentJobState.Completed => JobState.Completed,
        AgentJobState.Failed => JobState.Failed,
        AgentJobState.Canceled => JobState.Canceled,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown job state."),
    };

    private void PublishJobState(string jobId)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(jobId, out var job))
                PublishJobStateLocked(job);
        }
    }

    private void PublishJobStateLocked(AgentJobRecord job) =>
        events.PublishTo(job.ClientId, AgentEventTypes.JobState,
            JsonSerializer.Serialize(ToDto(job, Schedule()), RemoteJsonContext.Default.AgentJob));

    private void PublishQueueState(bool force)
    {
        lock (_lock)
            PublishQueueStateLocked(force);
    }

    private void PublishQueueStateLocked(bool force)
    {
        var now = timeProvider.GetTimestamp();
        if (!force && timeProvider.GetElapsedTime(_lastQueueEvent, now) < QueueEventInterval)
            return;
        _lastQueueEvent = now;
        events.PublishPerClient(AgentEventTypes.QueueState,
            clientId => JsonSerializer.Serialize(QueueStateFor(clientId), RemoteJsonContext.Default.QueueState));
    }

    private void TryDeleteFile(string? path)
    {
        if (path is null)
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}
