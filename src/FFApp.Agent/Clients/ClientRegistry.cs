using System.Collections.Immutable;

namespace FFApp.Agent.Clients;

/// <summary>
/// The current client list. Reloads <c>clients.json</c> when it changes, so adding or removing a
/// client needs no restart.
/// </summary>
public sealed class ClientRegistry(ClientStore store, TimeProvider timeProvider, ILogger<ClientRegistry> logger)
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    private readonly Lock _lock = new();
    private ImmutableArray<AgentClient> _clients = [];
    private DateTime _loadedWriteTime = DateTime.MinValue;
    private long _lastCheck;
    private bool _loaded;

    public ImmutableArray<AgentClient> Clients
    {
        get
        {
            Refresh();
            lock (_lock)
                return _clients;
        }
    }

    /// <summary>
    /// True when the client list comes from an existing file. A missing file must not be read as
    /// "every client was removed", which would delete all jobs.
    /// </summary>
    public bool HasClientFile => File.Exists(store.FilePath);

    public AgentClient? Authenticate(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length == 0)
            return null;
        foreach (var client in Clients)
        {
            if (ClientTokens.Matches(token, client.TokenSha256))
                return client;
        }
        return null;
    }

    public AgentClient? Find(string clientId) =>
        Clients.FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.Ordinal));

    /// <summary>Reloads the file when its modification time changed since the last load.</summary>
    public void Refresh(bool force = false)
    {
        lock (_lock)
        {
            if (!force && _loaded && timeProvider.GetElapsedTime(_lastCheck) < CheckInterval)
                return;
            _lastCheck = timeProvider.GetTimestamp();

            var writeTime = File.Exists(store.FilePath) ? File.GetLastWriteTimeUtc(store.FilePath) : DateTime.MinValue;
            if (_loaded && writeTime == _loadedWriteTime && !force)
                return;

            try
            {
                _clients = store.Load();
                _loadedWriteTime = writeTime;
                _loaded = true;
                logger.LogInformation("Loaded {ClientCount} clients from {ClientsFile}", _clients.Length, store.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // Keep the previous list: a file being rewritten must not lock every client out.
                logger.LogError(ex, "Could not read {ClientsFile}", store.FilePath);
            }
        }
    }
}
