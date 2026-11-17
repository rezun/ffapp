namespace FFApp.Agent;

/// <summary>Agent configuration, bound from the <c>Agent</c> section of <c>appsettings.json</c>.</summary>
public sealed class AgentOptions
{
    public static readonly string SectionName = "Agent";

    public static readonly string DefaultConfigDirectory = "/etc/ffapp-agent";

    public string Urls { get; set; } = "http://0.0.0.0:8737";

    public string WorkDirectory { get; set; } = "/var/lib/ffapp-agent";

    /// <summary>Space for uploaded sources and results, across all clients.</summary>
    public double QuotaGiB { get; set; } = 500;

    /// <summary>Prepared for later versions; the agent currently runs one job at a time.</summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>Days after which unacknowledged results and unused sources are deleted.</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>Allowlist of folders for results kept on the server, keyed by display name.</summary>
    public Dictionary<string, string> OutputRoots { get; set; } = new(StringComparer.Ordinal);

    public AgentToolOptions Tools { get; set; } = new();

    public string ClientsFile { get; set; } = Path.Combine(DefaultConfigDirectory, "clients.json");

    /// <summary>Base URL of release downloads; <c>v{version}/{asset}</c> is appended.</summary>
    public string ReleaseBaseUrl { get; set; } = "https://github.com/rezun/ffapp/releases/download";

    public long QuotaBytes => (long)(QuotaGiB * 1024 * 1024 * 1024);
}

public sealed class AgentToolOptions
{
    public string Ffmpeg { get; set; } = "";
    public string Ffprobe { get; set; } = "";
    public string Mkvpropedit { get; set; } = "";
}
