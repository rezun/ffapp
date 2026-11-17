using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

using FFApp.Agent.Events;
using FFApp.Agent.Jobs;
using FFApp.Agent.Storage;
using FFApp.Remote.Contracts;
using Microsoft.Extensions.Options;

namespace FFApp.Agent.Updates;

/// <summary>Persisted update state (<c>update.json</c>), shared with <c>ffapp-agent startup-check</c>.</summary>
public sealed record UpdateRecord(
    UpdateStateKind State,
    string? TargetVersion = null,
    string? PreviousVersion = null,
    int StartAttempts = 0,
    string? Message = null);

/// <summary>
/// Installs exactly the version the app asks for. The agent finishes the running job first,
/// downloads and verifies the release archive, switches <c>current</c> to it and exits; systemd
/// starts the new version. <c>startup-check</c> rolls back when the new version fails to start
/// twice in a row.
/// </summary>
public sealed partial class UpdateService(
    AgentPaths paths,
    AgentJobQueue queue,
    EventHub events,
    IHttpClientFactory httpClientFactory,
    IHostApplicationLifetime lifetime,
    IOptions<AgentOptions> options,
    ILogger<UpdateService> logger) : BackgroundService
{
    public static readonly string HttpClientName = "releases";
    private static readonly string ExecutableName = "ffapp-agent";
    private static readonly string ChecksumFileName = "SHA256SUMS";

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private UpdateRecord _state = new(UpdateStateKind.Idle);

    [GeneratedRegex(@"^\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();

    public UpdateStatus Status
    {
        get
        {
            lock (_lock)
                return new UpdateStatus(_state.State, _state.TargetVersion, _state.Message);
        }
    }

    /// <summary>True when the agent runs from the layout <c>install.sh</c> creates.</summary>
    public bool IsManagedInstall =>
        new FileInfo(paths.Current).LinkTarget is not null
        && Environment.ProcessPath is { } process
        && Path.GetFullPath(process).StartsWith(Path.GetFullPath(paths.Versions) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    public UpdateStatus Request(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || !VersionPattern().IsMatch(version))
            throw AgentException.Invalid("version must be a release version such as 1.15.0.");
        if (!IsManagedInstall)
        {
            throw AgentException.Conflict(ErrorCodes.UpdateUnavailable,
                "This agent was not installed with ffapp-agent-install.sh and cannot update itself.");
        }

        lock (_lock)
        {
            if (_state.State is UpdateStateKind.Downloading or UpdateStateKind.Restarting)
                throw AgentException.Conflict(ErrorCodes.InvalidState, "An update is already being installed.");
            if (string.Equals(version, AgentVersion.Current, StringComparison.Ordinal))
            {
                SetState(new UpdateRecord(UpdateStateKind.Idle, Message: $"Version {version} is already installed."));
                return Status;
            }
            SetState(new UpdateRecord(UpdateStateKind.WaitingForJobs, version));
        }
        queue.StartsBlocked = true;
        _signal.Release();
        return Status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadPersistedOutcome();
        while (!stoppingToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(stoppingToken);
            string? target;
            lock (_lock)
                target = _state.State == UpdateStateKind.WaitingForJobs ? _state.TargetVersion : null;
            if (target is null)
                continue;

            while (queue.HasActiveJobs)
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            try
            {
                lock (_lock)
                    SetState(new UpdateRecord(UpdateStateKind.Downloading, target));
                await InstallAsync(target, stoppingToken);
                lock (_lock)
                {
                    SetState(new UpdateRecord(UpdateStateKind.Restarting, target, AgentVersion.Current));
                    AtomicFile.WriteJson(paths.UpdateState, _state, AgentJsonContext.Default.UpdateRecord);
                }
                SwitchCurrent(target);
                logger.LogInformation("Installed agent {Version}; restarting", target);
                lifetime.StopApplication();
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                           or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogError(ex, "Agent update to {Version} failed", target);
                lock (_lock)
                    SetState(new UpdateRecord(UpdateStateKind.Failed, target, Message: ex.Message));
                queue.StartsBlocked = false;
            }
        }
    }

    /// <summary>Downloads, verifies and extracts a release into <c>versions/&lt;version&gt;</c>.</summary>
    private async Task InstallAsync(string version, CancellationToken ct)
    {
        var asset = $"ffapp-agent-{version}-{AgentVersion.RuntimeIdentifier}.tar.gz";
        var baseUrl = $"{options.Value.ReleaseBaseUrl.TrimEnd('/')}/v{version}/";
        var client = httpClientFactory.CreateClient(HttpClientName);

        var sums = await client.GetStringAsync(new Uri(baseUrl + ChecksumFileName), ct);
        var expected = ParseChecksum(sums, asset)
            ?? throw new InvalidDataException($"{ChecksumFileName} of version {version} has no entry for {asset}.");

        Directory.CreateDirectory(paths.Versions);
        var archive = Path.Combine(paths.Versions, asset + ".download");
        try
        {
            await using (var response = await client.GetStreamAsync(new Uri(baseUrl + asset), ct))
            await using (var file = File.Create(archive))
                await response.CopyToAsync(file, ct);

            string actual;
            await using (var file = File.OpenRead(archive))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The checksum of {asset} does not match {ChecksumFileName}.");

            var target = Path.Combine(paths.Versions, version);
            var staging = target + ".staging";
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            await using (var file = File.OpenRead(archive))
            await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true, ct);

            var executable = Path.Combine(staging, ExecutableName);
            if (!File.Exists(executable))
                throw new InvalidDataException($"{asset} does not contain {ExecutableName}.");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    internal static string? ParseChecksum(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.Ordinal))
                return parts[0];
        }
        return null;
    }

    /// <summary>Points <c>previous</c> at the running version and <c>current</c> at the new one.</summary>
    private void SwitchCurrent(string version)
    {
        ReplaceSymlink(paths.Previous, Path.Combine(paths.Versions, AgentVersion.Current));
        ReplaceSymlink(paths.Current, Path.Combine(paths.Versions, version));
    }

    /// <summary>Replaces a symlink atomically by renaming a fresh link over it.</summary>
    internal static void ReplaceSymlink(string link, string target)
    {
        var temporary = link + ".new";
        if (new FileInfo(temporary).LinkTarget is not null || File.Exists(temporary))
            File.Delete(temporary);
        File.CreateSymbolicLink(temporary, target);
        // File.Move treats a link to a directory as a directory; rename(2) replaces the link itself.
        if (NativeMethods.Rename(temporary, link) != 0)
            throw new IOException($"Could not switch {link} to {target} (errno {Marshal.GetLastPInvokeError()}).");
    }

    /// <summary>
    /// Records the outcome of an update after a restart: success when this is the requested
    /// version, otherwise the failure that <c>startup-check</c> recorded.
    /// </summary>
    private void LoadPersistedOutcome()
    {
        UpdateRecord? persisted;
        try
        {
            persisted = AtomicFile.ReadJson(paths.UpdateState, AgentJsonContext.Default.UpdateRecord);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Ignoring unreadable update state");
            return;
        }
        if (persisted is null)
            return;

        lock (_lock)
        {
            if (persisted.State == UpdateStateKind.Restarting
                && string.Equals(persisted.TargetVersion, AgentVersion.Current, StringComparison.Ordinal))
            {
                SetState(new UpdateRecord(UpdateStateKind.Idle, Message: $"Updated to {AgentVersion.Current}."));
                File.Delete(paths.UpdateState);
                DeleteOldVersions();
            }
            else if (persisted.State == UpdateStateKind.Failed)
            {
                SetState(persisted);
                File.Delete(paths.UpdateState);
            }
        }
    }

    /// <summary>Keeps <c>current</c> and <c>previous</c>; older versions are deleted.</summary>
    private void DeleteOldVersions()
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in new[] { paths.Current, paths.Previous })
        {
            if (new FileInfo(link).LinkTarget is { } target)
                keep.Add(Path.GetFileName(target.TrimEnd('/')));
        }
        if (!Directory.Exists(paths.Versions))
            return;
        foreach (var directory in Directory.EnumerateDirectories(paths.Versions))
        {
            if (keep.Contains(Path.GetFileName(directory)))
                continue;
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete old version {Directory}", directory);
            }
        }
    }

    private void SetState(UpdateRecord state)
    {
        _state = state;
        events.PublishToAll(AgentEventTypes.UpdateState,
            JsonSerializer.Serialize(new UpdateStatus(state.State, state.TargetVersion, state.Message),
                RemoteJsonContext.Default.UpdateStatus));
    }
}

internal static partial class NativeMethods
{
    [LibraryImport("libc", EntryPoint = "rename", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Rename(string oldPath, string newPath);
}
