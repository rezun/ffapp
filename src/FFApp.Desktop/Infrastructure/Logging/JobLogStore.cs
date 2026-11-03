using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace FFApp.Infrastructure.Logging;

/// <param name="Directory">Folder that holds one <c>.log</c> file per job.</param>
/// <param name="Retention">Job logs older than this are deleted when the first job of a session starts.</param>
/// <param name="ApplicationLabel">Name and version written into each file's first line.</param>
public sealed record JobLogOptions(string Directory, TimeSpan Retention, string ApplicationLabel);

/// <summary>Keeps the full FFmpeg (and helper tool) output of every job in its own file.</summary>
public interface IJobLogStore
{
    /// <summary>
    /// Creates the log file for one job. Never throws: when the file cannot be created, the returned log
    /// discards its lines and <see cref="JobLog.FilePath"/> is null, so a logging problem never stops a job.
    /// </summary>
    /// <param name="jobName">Usually the output file name; it becomes part of the log file name.</param>
    JobLog Start(string jobName);
}

public sealed partial class JobLogStore(
    JobLogOptions options,
    TimeProvider timeProvider,
    ILogger<JobLogStore> logger) : IJobLogStore
{
    private static readonly int MaxNameLength = 80;
    private static readonly int MaxCollisionSuffix = 100;
    private static readonly string LogExtension = ".log";

    private readonly Lock _pruneLock = new();
    private bool _pruned;

    public JobLog Start(string jobName)
    {
        ArgumentNullException.ThrowIfNull(jobName);

        try
        {
            Directory.CreateDirectory(options.Directory);
            PruneOnce();

            var started = timeProvider.GetLocalNow();
            var stream = CreateUniqueFile(started, jobName, out var path);
            var log = new JobLog(path, stream, logger);
            log.WriteLine($"# {options.ApplicationLabel} job log");
            log.WriteField("Job", jobName);
            log.WriteField("Started", started.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            return log;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStartFailed(ex, jobName, options.Directory);
            return JobLog.Discarding;
        }
    }

    private FileStream CreateUniqueFile(DateTimeOffset started, string jobName, out string path)
    {
        var stem = string.Create(CultureInfo.InvariantCulture, $"{started:yyyy-MM-dd_HH-mm-ss}_{SafeName(jobName)}");
        for (var attempt = 1; ; attempt++)
        {
            var name = attempt == 1 ? stem : string.Create(CultureInfo.InvariantCulture, $"{stem}-{attempt}");
            path = Path.Combine(options.Directory, name + LogExtension);
            try
            {
                // CreateNew fails instead of overwriting when two jobs start in the same second.
                return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            catch (IOException) when (File.Exists(path) && attempt < MaxCollisionSuffix)
            {
            }
        }
    }

    internal static string SafeName(string jobName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(Math.Min(jobName.Length, MaxNameLength));
        foreach (var c in jobName.AsSpan().Trim())
        {
            if (builder.Length == MaxNameLength)
                break;
            builder.Append(Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) || c is '/' or '\\' or ':' ? '_' : c);
        }
        return builder.Length == 0 ? "job" : builder.ToString();
    }

    /// <summary>Deletes expired job logs once per session, before the first new one is written.</summary>
    private void PruneOnce()
    {
        lock (_pruneLock)
        {
            if (_pruned)
                return;
            _pruned = true;
        }

        var cutoff = timeProvider.GetUtcNow().UtcDateTime - options.Retention;
        foreach (var file in Directory.EnumerateFiles(options.Directory, "*" + LogExtension))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogPruneFailed(ex, file);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create a job log for {JobName} in {Directory}")]
    private partial void LogStartFailed(Exception ex, string jobName, string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete expired job log {Path}")]
    private partial void LogPruneFailed(Exception ex, string path);
}

/// <summary>
/// One job's log file. Lines are flushed as they arrive, so the file is complete up to the last line
/// even when the app crashes mid-job. Safe to call from FFmpeg's output reader threads.
/// </summary>
public sealed partial class JobLog : IDisposable
{
    private static readonly int FieldLabelWidth = 9;

    private readonly Lock _lock = new();
    private readonly ILogger? _logger;
    private StreamWriter? _writer;

    /// <summary>A log that ignores every line; used when the file could not be created.</summary>
    public static JobLog Discarding => new(null, null, null);

    internal JobLog(string? filePath, Stream? stream, ILogger? logger)
    {
        FilePath = filePath;
        _logger = logger;
        if (stream is not null)
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    }

    /// <summary>The file this log writes to, or null when it discards its lines.</summary>
    public string? FilePath { get; }

    public void WriteLine(string line)
    {
        lock (_lock)
        {
            if (_writer is null)
                return;
            try
            {
                _writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // A full disk must not fail the job; stop writing and note it once in the app log.
                _writer.Dispose();
                _writer = null;
                if (_logger is not null)
                    LogWriteFailed(_logger, ex, FilePath ?? "");
            }
        }
    }

    /// <summary>Writes an aligned <c>Label: value</c> header line.</summary>
    public void WriteField(string label, string value) =>
        WriteLine($"{(label + ":").PadRight(FieldLabelWidth + 1)} {value}");

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                _writer?.Dispose();
            }
            catch (IOException ex)
            {
                if (_logger is not null)
                    LogWriteFailed(_logger, ex, FilePath ?? "");
            }
            _writer = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing job log {Path} failed; the rest of the job is not logged")]
    private static partial void LogWriteFailed(ILogger logger, Exception ex, string path);
}
