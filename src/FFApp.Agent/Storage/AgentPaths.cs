using Microsoft.Extensions.Options;

namespace FFApp.Agent.Storage;

/// <summary>Layout of the agent's work directory.</summary>
public sealed class AgentPaths(IOptions<AgentOptions> options)
{
    public string Root { get; } = Path.GetFullPath(options.Value.WorkDirectory);

    public string Sources => Path.Combine(Root, "sources");
    public string Jobs => Path.Combine(Root, "jobs");
    public string Versions => Path.Combine(Root, "versions");
    public string Current => Path.Combine(Root, "current");
    public string Previous => Path.Combine(Root, "previous");
    public string UpdateState => Path.Combine(Root, "update.json");
    public string Throughput => Path.Combine(Root, "throughput.json");

    public string SourceDirectory(string sourceId) => Path.Combine(Sources, sourceId);
    public string SourceMeta(string sourceId) => Path.Combine(SourceDirectory(sourceId), "meta.json");

    public string JobDirectory(string jobId) => Path.Combine(Jobs, jobId);
    public string JobFile(string jobId) => Path.Combine(JobDirectory(jobId), "job.json");
    public string JobWork(string jobId) => Path.Combine(JobDirectory(jobId), "work");
    public string JobLog(string jobId) => Path.Combine(JobDirectory(jobId), "job.log");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Sources);
        Directory.CreateDirectory(Jobs);
    }
}
