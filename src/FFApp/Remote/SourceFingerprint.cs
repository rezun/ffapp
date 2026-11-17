using System.Globalization;
using System.Security.Cryptography;

namespace FFApp.Remote;

/// <summary>
/// Identifies a source file cheaply: size, modification time and the hash of its first and last
/// 16 MiB. The agent uses it to skip uploading a file it already has.
/// </summary>
public static class SourceFingerprint
{
    private static readonly int SampleBytes = 16 * 1024 * 1024;

    public static async Task<string> ComputeAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("The source file is no longer available.", path);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"ffapp-source-v1|{info.Length}|{new DateTimeOffset(info.LastWriteTimeUtc).UtcTicks}|")));

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var buffer = new byte[Math.Min(SampleBytes, info.Length)];
        await stream.ReadExactlyAsync(buffer, ct);
        hash.AppendData(buffer);
        if (info.Length > SampleBytes)
        {
            stream.Seek(Math.Max(SampleBytes, info.Length - SampleBytes), SeekOrigin.Begin);
            var tail = new byte[info.Length - stream.Position];
            await stream.ReadExactlyAsync(tail, ct);
            hash.AppendData(tail);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
