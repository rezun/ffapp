using System.Net;
using System.Security.Cryptography;

using FFApp.Agent.Jobs;
using FFApp.Agent.Storage;
using FFApp.Agent.Updates;
using FFApp.Remote.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FFApp.Agent.Tests.Agent;

public sealed class AgentMaintenanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ffapp-agent-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task RemovedClient_LosesJobsSourcesAndResults_OthersKeepTheirs()
    {
        await using var host = new AgentHost(_directory, new FakeProcessRunner());
        var a = host.Client(AgentHost.TokenA);
        var b = host.Client(AgentHost.TokenB);
        var data = RandomNumberGenerator.GetBytes(1000);
        var aJob = await a.SubmitAsync(AgentApi.JobRequest(await a.UploadAsync(data)));
        var bJob = await b.SubmitAsync(AgentApi.JobRequest(await b.UploadAsync(data)));
        await a.WaitForStateAsync(aJob.JobId, AgentJobState.Completed);
        await b.WaitForStateAsync(bJob.JobId, AgentJobState.Completed);

        host.Services.GetRequiredService<AgentJobQueue>().PurgeClients(new HashSet<string>(["client-b"]));

        Directory.Exists(Path.Combine(host.WorkDirectory, "jobs", aJob.JobId)).Should().BeFalse();
        Directory.Exists(Path.Combine(host.WorkDirectory, "sources", aJob.SourceId)).Should().BeFalse();
        (await b.GetJobAsync(bJob.JobId)).State.Should().Be(AgentJobState.Completed);
        (await a.GetAsync($"jobs/{aJob.JobId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void StartupCheck_RollsBackAfterTwoFailedStarts()
    {
        var paths = new AgentPaths(Options.Create(new AgentOptions { WorkDirectory = _directory }));
        Directory.CreateDirectory(Path.Combine(paths.Versions, "1.0.0"));
        Directory.CreateDirectory(Path.Combine(paths.Versions, "1.1.0"));
        UpdateService.ReplaceSymlink(paths.Previous, Path.Combine(paths.Versions, "1.0.0"));
        UpdateService.ReplaceSymlink(paths.Current, Path.Combine(paths.Versions, "1.1.0"));
        File.WriteAllText(paths.UpdateState, """{"state":"restarting","targetVersion":"1.1.0","previousVersion":"1.0.0"}""");

        StartupCheck.Run(paths, TextWriter.Null).Should().Be(0);
        StartupCheck.Run(paths, TextWriter.Null).Should().Be(0);
        new FileInfo(paths.Current).LinkTarget.Should().EndWith("1.1.0", "two start attempts are allowed");

        StartupCheck.Run(paths, TextWriter.Null).Should().Be(0);

        new FileInfo(paths.Current).LinkTarget.Should().EndWith("1.0.0");
        File.ReadAllText(paths.UpdateState).Should().Contain("\"failed\"").And.Contain("restored 1.0.0");
    }

    [Fact]
    public void ParseChecksum_FindsAssetInSha256Sums()
    {
        var sums = "abc123  ffapp-agent-1.15.0-linux-x64.tar.gz\ndef456 *ffapp-agent-1.15.0-linux-arm64.tar.gz\n";

        UpdateService.ParseChecksum(sums, "ffapp-agent-1.15.0-linux-arm64.tar.gz").Should().Be("def456");
        UpdateService.ParseChecksum(sums, "ffapp-agent-1.15.0-linux-riscv.tar.gz").Should().BeNull();
    }
}
