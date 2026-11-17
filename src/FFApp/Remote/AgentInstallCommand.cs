namespace FFApp.Remote;

/// <summary>A choice for where a server job's result goes.</summary>
public sealed record ResultOption(string Label, FFApp.Remote.Contracts.ResultMode Mode, string? Root)
{
    public override string ToString() => Label;
}

/// <summary>The one-line installer command for a given agent version.</summary>
public static class AgentInstallCommand
{
    private static readonly string ReleasesUrl = "https://github.com/rezun/ffapp/releases";

    /// <summary>Installs the latest release on a new server.</summary>
    public static string Latest => $"curl -fsSL {ReleasesUrl}/latest/download/ffapp-agent-install.sh | sudo bash";

    public static string For(string version) =>
        $"curl -fsSL {ReleasesUrl}/download/v{version}/ffapp-agent-install.sh | sudo bash -s -- --version {version}";
}
