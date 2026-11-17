using FFApp.Remote;
using FluentAssertions;

namespace FFApp.Tests.Remote;

public sealed class RemoteSettingsTests
{
    [Theory]
    [InlineData("nas", "http://nas:8737/")]
    [InlineData("nas.local:9000", "http://nas.local:9000/")]
    [InlineData(" 192.168.1.20 ", "http://192.168.1.20:8737/")]
    [InlineData("http://nas", "http://nas/")]
    [InlineData("https://nas.example/api/v1?x=1", "https://nas.example/")]
    [InlineData("ftp://nas", null)]
    [InlineData("", null)]
    public void ParseBaseUrl_AcceptsHostsPortsAndUrls(string input, string? expected)
    {
        AgentEndpoint.ParseBaseUrl(input)?.ToString().Should().Be(expected);
    }

    [Fact]
    public void FileSecretStore_KeepsTokenReadableOnlyByTheUser()
    {
        if (OperatingSystem.IsWindows())
            return;
        var directory = Path.Combine(Path.GetTempPath(), $"ffapp-secrets-{Guid.NewGuid():N}");
        try
        {
            var store = new FileSecretStore(directory);
            store.Write("token", "first");
            store.Write("token", "second");

            store.Read("token").Should().Be("second");
            File.GetUnixFileMode(Path.Combine(directory, "token")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(directory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            store.Delete("token");
            store.Read("token").Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
