using System.Reflection;
using System.Text.Json;

using FFApp.Core.Models;
using FFApp.Remote.Contracts;
using Microsoft.Extensions.Logging;

namespace FFApp.Remote;

public enum AgentConnectionState
{
    NotConfigured,
    Connecting,
    Ready,
    Unreachable,
    Incompatible,
}

/// <summary>
/// What the app knows about the server agent. <see cref="Message"/> explains why the server target
/// is unavailable, or carries a non-blocking hint such as a version difference.
/// </summary>
public sealed record AgentConnectionStatus(
    AgentConnectionState State,
    string Message,
    AgentInfo? Info = null,
    bool OffersUpdate = false)
{
    public bool IsReady => State == AgentConnectionState.Ready;
}

public static class AppVersion
{
    /// <summary>The running app's release version, without build metadata.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}

/// <summary>
/// Keeps the connection to the server agent: checks <c>GET /info</c>, follows the event stream
/// and reconnects with backoff. Events are raised on background threads.
/// </summary>
public sealed class RemoteAgentConnection(
    RemoteAgentClient client,
    ISecretStore secrets,
    TimeProvider timeProvider,
    ILogger<RemoteAgentConnection> logger) : IAsyncDisposable
{
    private static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(70);

    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _disposed = new();
    private CancellationTokenSource? _loopCts;
    private Task _loop = Task.CompletedTask;
    private AgentEndpoint? _endpoint;
    private AgentConnectionStatus _status = new(AgentConnectionState.NotConfigured, "No server agent is set up. Add one in Settings → Server agent.");
    private QueueState? _load;
    private UpdateStatus? _update;

    public event Action<AgentConnectionStatus>? StatusChanged;
    public event Action<QueueState>? LoadChanged;
    public event Action<UpdateStatus>? UpdateChanged;
    public event Action<AgentJob>? JobChanged;
    public event Action<JobProgressEvent>? JobProgressReceived;
    public event Action<JobLogEvent>? JobLogReceived;

    /// <summary>Raised after the event stream (re)connected; listeners reconcile through <c>GET /jobs</c>.</summary>
    public event Action? Connected;

    public AgentConnectionStatus Status
    {
        get { lock (_lock) return _status; }
    }

    public QueueState? Load
    {
        get { lock (_lock) return _load; }
    }

    public UpdateStatus? Update
    {
        get { lock (_lock) return _update; }
    }

    public AgentEndpoint? Endpoint
    {
        get { lock (_lock) return _endpoint; }
    }

    public RemoteAgentClient Client => client;

    /// <summary>Applies new settings and restarts the connection.</summary>
    public async Task ConfigureAsync(RemoteAgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Task previous;
        lock (_lock)
        {
            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _loopCts = null;
            previous = _loop;
        }
        try
        {
            await previous;
        }
        catch (OperationCanceledException)
        {
            // The previous connection loop ends by cancellation.
        }

        var url = AgentEndpoint.ParseBaseUrl(settings.BaseUrl);
        string? token;
        try
        {
            token = url is null ? null : secrets.Read(SecretNames.AgentToken);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Reading the agent token failed");
            token = null;
        }

        lock (_lock)
        {
            _endpoint = url is not null && !string.IsNullOrEmpty(token) ? new AgentEndpoint(url, token) : null;
            _load = null;
            if (_endpoint is null)
            {
                SetStatus(new AgentConnectionStatus(AgentConnectionState.NotConfigured,
                    url is null ? "No server agent is set up. Add one in Settings → Server agent."
                        : "The server agent token is missing. Enter it in Settings → Server agent."));
                return;
            }
            SetStatus(new AgentConnectionStatus(AgentConnectionState.Connecting, "Connecting to the server agent…"));
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token);
            var endpoint = _endpoint;
            var token2 = _loopCts.Token;
            _loop = Task.Run(() => RunAsync(endpoint, token2), token2);
        }
    }

    /// <summary>Checks an agent without saving anything, for "Test connection".</summary>
    public async Task<AgentConnectionStatus> TestAsync(AgentEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        try
        {
            return Evaluate(await client.GetInfoAsync(endpoint, ct));
        }
        catch (RemoteAgentException ex)
        {
            return new AgentConnectionStatus(AgentConnectionState.Unreachable, ex.Message);
        }
    }

    /// <summary>The endpoint for a server job, or an exception explaining why the server is unusable.</summary>
    public AgentEndpoint RequireEndpoint()
    {
        lock (_lock)
        {
            if (_endpoint is null)
                throw new RemoteAgentException(_status.Message);
            if (_status.State == AgentConnectionState.Incompatible)
                throw new RemoteAgentException(_status.Message);
            return _endpoint;
        }
    }

    public async Task<UpdateStatus> RequestUpdateAsync(CancellationToken ct)
    {
        var endpoint = Endpoint ?? throw new RemoteAgentException(Status.Message);
        var status = await client.RequestUpdateAsync(endpoint, AppVersion.Current, ct);
        lock (_lock)
            _update = status;
        UpdateChanged?.Invoke(status);
        return status;
    }

    /// <summary>Checks <c>GET /info</c> now, for example after an agent update.</summary>
    public async Task<AgentConnectionStatus> RefreshAsync(CancellationToken ct)
    {
        var endpoint = Endpoint;
        if (endpoint is null)
            return Status;
        try
        {
            var status = Evaluate(await client.GetInfoAsync(endpoint, ct));
            lock (_lock)
                SetStatus(status);
            return status;
        }
        catch (RemoteAgentException ex)
        {
            var status = new AgentConnectionStatus(AgentConnectionState.Unreachable, ex.Message);
            lock (_lock)
                SetStatus(status);
            return status;
        }
    }

    internal static AgentConnectionStatus Evaluate(AgentInfo info)
    {
        if (info.ProtocolVersion != Protocol.Version)
        {
            return new AgentConnectionStatus(AgentConnectionState.Incompatible,
                $"The server agent {info.AgentVersion} speaks a different protocol than this app. Update the agent to {AppVersion.Current}.",
                info, OffersUpdate: true);
        }
        if (!string.Equals(info.AgentVersion, AppVersion.Current, StringComparison.Ordinal))
        {
            return new AgentConnectionStatus(AgentConnectionState.Ready,
                $"The server agent runs {info.AgentVersion}; this app is {AppVersion.Current}.", info, OffersUpdate: true);
        }
        return new AgentConnectionStatus(AgentConnectionState.Ready, $"Connected to the server agent {info.AgentVersion}.", info);
    }

    private async Task RunAsync(AgentEndpoint endpoint, CancellationToken ct)
    {
        var delay = MinimumRetryDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = Evaluate(await client.GetInfoAsync(endpoint, ct));
                lock (_lock)
                    SetStatus(status);
                delay = MinimumRetryDelay;
                await FollowEventsAsync(endpoint, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is RemoteAgentException or HttpRequestException or IOException
                                           or OperationCanceledException or JsonException)
            {
                logger.LogInformation("Server agent connection lost: {Message}", ex.Message);
                lock (_lock)
                {
                    if (_status.State != AgentConnectionState.Incompatible)
                        SetStatus(new AgentConnectionStatus(AgentConnectionState.Unreachable, ex is RemoteAgentException
                            ? ex.Message : $"The server agent is not reachable: {ex.Message}", _status.Info));
                }
            }

            await Task.Delay(delay, timeProvider, ct);
            delay = TimeSpan.FromTicks(Math.Min(MaximumRetryDelay.Ticks, delay.Ticks * 2));
        }
    }

    /// <summary>Reads the event stream; a silent connection counts as lost, since the agent sends heartbeats.</summary>
    private async Task FollowEventsAsync(AgentEndpoint endpoint, CancellationToken ct)
    {
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
        silence.CancelAfter(SilenceLimit);
        var connected = false;
        await foreach (var item in client.StreamEventsAsync(endpoint, silence.Token))
        {
            silence.CancelAfter(SilenceLimit);
            if (!connected)
            {
                connected = true;
                Connected?.Invoke();
            }
            Dispatch(item.EventType, item.Data);
        }
    }

    private void Dispatch(string eventType, string data)
    {
        try
        {
            if (eventType == AgentEventTypes.JobState)
            {
                if (JsonSerializer.Deserialize(data, RemoteJsonContext.Default.AgentJob) is { } job)
                    JobChanged?.Invoke(job);
            }
            else if (eventType == AgentEventTypes.JobProgress)
            {
                if (JsonSerializer.Deserialize(data, RemoteJsonContext.Default.JobProgressEvent) is { } progress)
                    JobProgressReceived?.Invoke(progress);
            }
            else if (eventType == AgentEventTypes.JobLog)
            {
                if (JsonSerializer.Deserialize(data, RemoteJsonContext.Default.JobLogEvent) is { } log)
                    JobLogReceived?.Invoke(log);
            }
            else if (eventType == AgentEventTypes.QueueState)
            {
                if (JsonSerializer.Deserialize(data, RemoteJsonContext.Default.QueueState) is { } load)
                {
                    lock (_lock)
                        _load = load;
                    LoadChanged?.Invoke(load);
                }
            }
            else if (eventType == AgentEventTypes.UpdateState)
            {
                if (JsonSerializer.Deserialize(data, RemoteJsonContext.Default.UpdateStatus) is { } update)
                {
                    lock (_lock)
                        _update = update;
                    UpdateChanged?.Invoke(update);
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Ignoring unreadable {EventType} event", eventType);
        }
    }

    private void SetStatus(AgentConnectionStatus status)
    {
        if (_status == status)
            return;
        _status = status;
        StatusChanged?.Invoke(status);
    }

    public async ValueTask DisposeAsync()
    {
        await _disposed.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Expected when the loop is stopped.
        }
        _disposed.Dispose();
        _loopCts?.Dispose();
    }
}
