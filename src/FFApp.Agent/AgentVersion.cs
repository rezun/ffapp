using System.Reflection;
using System.Runtime.InteropServices;

namespace FFApp.Agent;

public static class AgentVersion
{
    /// <summary>The release version, without build metadata.</summary>
    public static string Current { get; } = ReadVersion();

    /// <summary>Release asset architecture: <c>linux-x64</c> or <c>linux-arm64</c>.</summary>
    public static string RuntimeIdentifier => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "linux-x64",
        Architecture.Arm64 => "linux-arm64",
        var other => throw new PlatformNotSupportedException($"The agent does not support {other}."),
    };

    private static string ReadVersion()
    {
        var informational = typeof(AgentVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
