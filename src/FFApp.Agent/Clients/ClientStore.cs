using System.Collections.Immutable;
using System.Text.Json;

using FFApp.Agent.Storage;
using Microsoft.Extensions.Options;

namespace FFApp.Agent.Clients;

/// <summary>
/// Reads and writes <c>clients.json</c>. The agent only reads it; the <c>client</c> commands,
/// run as root, change it. The file keeps its owner and mode because it is rewritten in place.
/// </summary>
public sealed class ClientStore(IOptions<AgentOptions> options, TimeProvider timeProvider)
{
    private static readonly UnixFileMode NewFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    public string FilePath { get; } = options.Value.ClientsFile;

    public ImmutableArray<AgentClient> Load()
    {
        if (!File.Exists(FilePath))
            return [];
        using var stream = File.OpenRead(FilePath);
        return JsonSerializer.Deserialize(stream, AgentJsonContext.Default.ClientList)?.Clients ?? [];
    }

    /// <summary>Adds a client and returns its token, which is not stored anywhere.</summary>
    public (AgentClient Client, string Token) Add(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var clients = Load();
        if (clients.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A client named \"{name}\" already exists.");

        var token = ClientTokens.Generate();
        var client = new AgentClient(Guid.NewGuid().ToString("N"), name.Trim(), ClientTokens.Hash(token),
            timeProvider.GetUtcNow());
        Save(clients.Add(client));
        return (client, token);
    }

    public bool Remove(string name)
    {
        var clients = Load();
        var remaining = clients.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (remaining.Length == clients.Length)
            return false;
        Save(remaining);
        return true;
    }

    private void Save(ImmutableArray<AgentClient> clients)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ClientList(clients), AgentJsonContext.Default.ClientList);
        if (File.Exists(FilePath))
        {
            using var stream = new FileStream(FilePath, FileMode.Truncate, FileAccess.Write);
            stream.Write(bytes);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath)) ?? ".");
        var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            create.UnixCreateMode = NewFileMode;
        using (var stream = new FileStream(FilePath, create))
            stream.Write(bytes);
    }
}
