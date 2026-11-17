using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace FFApp.Agent.Clients;

/// <summary>A client allowed to use the agent. Only the SHA-256 hash of its token is stored.</summary>
public sealed record AgentClient(string ClientId, string Name, string TokenSha256, DateTimeOffset CreatedAt);

public sealed record ClientList(ImmutableArray<AgentClient> Clients, int SchemaVersion = 1);

internal static class ClientTokens
{
    private static readonly int TokenBytes = 32;

    public static string Generate() => Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool Matches(string token, string expectedHash)
    {
        var actual = Encoding.ASCII.GetBytes(Hash(token));
        var expected = Encoding.ASCII.GetBytes(expectedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
