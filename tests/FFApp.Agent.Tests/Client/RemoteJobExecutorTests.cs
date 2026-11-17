using System.Security.Cryptography;

using FFApp.Agent.Tests.Agent;
using FFApp.Core.Models;
using FFApp.Remote;
using FFApp.Remote.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFApp.Agent.Tests.Client;

/// <summary>The app's server lane against a real (in-memory) agent.</summary>
public sealed class RemoteJobExecutorTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ffapp-remote-tests-{Guid.NewGuid():N}");
    private readonly List<IAsyncDisposable> _disposables = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
            await disposable.DisposeAsync();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ServerJob_UploadsEncodesDownloadsAndAcknowledges()
    {
        var host = Track(new AgentHost(Path.Combine(_directory, "agent"), new FakeProcessRunner()));
        var (executor, store) = await CreateClientAsync(host);
        var (job, media) = LocalJob();
        File.WriteAllText(job.OutputPath, "an older file with the same name");
        var phases = new List<RemotePhase>();
        var log = new List<string>();

        var result = await executor.ExecuteAsync(new RemoteTarget("key-1", ResultMode.Download, DisplayLabel: "movie.mkv"),
            "movie.mkv", job, media, progress => { lock (phases) phases.Add(progress.Remote!.Phase); },
            line => { lock (log) log.Add(line); }, null, CancellationToken.None);

        result.State.Should().Be(JobState.Completed, string.Join(Environment.NewLine, log));
        result.Job.OutputPath.Should().NotBe(job.OutputPath, "an existing file is never overwritten");
        File.ReadAllBytes(result.Job.OutputPath).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        phases.Should().ContainInOrder(RemotePhase.Uploading, RemotePhase.WaitingForServer, RemotePhase.Done);
        store.Find("key-1").Should().BeNull("an acknowledged job is forgotten");
        Directory.EnumerateDirectories(Path.Combine(host.WorkDirectory, "jobs")).Should().BeEmpty("the agent cleaned up after the ack");
    }

    [Fact]
    public async Task AppRestart_DuringEncode_ContinuesAndDownloads()
    {
        var runner = new FakeProcessRunner { HoldJobs = true };
        var host = Track(new AgentHost(Path.Combine(_directory, "agent"), runner));
        var (job, media) = LocalJob();
        var target = new RemoteTarget("key-2", ResultMode.Download, DisplayLabel: "movie.mkv");

        var (first, store) = await CreateClientAsync(host);
        var run = first.ExecuteAsync(target, "movie.mkv", job, media, null, null, null, CancellationToken.None);
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        first.Detach();
        var detached = () => run;
        await detached.Should().ThrowAsync<OperationCanceledException>();
        store.Find("key-2")!.AgentJobId.Should().NotBeNull("the job keeps running on the server");

        var (second, _) = await CreateClientAsync(host);
        second.RestorableJobs().Should().ContainSingle(record => record.Key == "key-2");
        var resumed = second.ExecuteAsync(target, "movie.mkv", job, media, null, null, null, CancellationToken.None);
        runner.Release();
        var result = await resumed.WaitAsync(TimeSpan.FromSeconds(20));

        result.State.Should().Be(JobState.Completed);
        File.Exists(job.OutputPath).Should().BeTrue();
        runner.Commands.Count(command => command.Protocol == ProcessProtocol.Ffmpeg).Should().Be(1, "the restarted app does not submit the job again");
    }

    [Fact]
    public async Task CanceledServerJob_IsCanceledOnTheAgent()
    {
        var runner = new FakeProcessRunner { HoldJobs = true };
        var host = Track(new AgentHost(Path.Combine(_directory, "agent"), runner));
        var (executor, store) = await CreateClientAsync(host);
        var (job, media) = LocalJob();
        using var cts = new CancellationTokenSource();

        var run = executor.ExecuteAsync(new RemoteTarget("key-3", ResultMode.Download), "movie.mkv", job, media,
            null, null, null, cts.Token);
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        var canceled = () => run;
        await canceled.Should().ThrowAsync<OperationCanceledException>();

        var agentJobId = store.Find("key-3")!.AgentJobId!;
        var agent = host.Client(AgentHost.TokenA);
        await agent.WaitForStateAsync(agentJobId, AgentJobState.Canceled);
        store.Find("key-3")!.Finished.Should().BeTrue("a canceled job waits for Run again or removal");
    }

    private (JobDefinition Job, SourceMediaInfo Media) LocalJob()
    {
        var local = Path.Combine(_directory, "mac");
        Directory.CreateDirectory(local);
        var source = Path.Combine(local, "movie.mkv");
        File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(500_000));
        var request = AgentApi.JobRequest("unused");
        return (request.Job with { SourcePath = source, OutputPath = Path.Combine(local, "movie - AV1.mkv") },
            request.MediaInfo with { SourcePath = source });
    }

    private async Task<(RemoteJobExecutor Executor, RemoteJobStore Store)> CreateClientAsync(AgentHost host)
    {
        var client = new RemoteAgentClient(new TestServerHttpClientFactory(host));
        var connection = new RemoteAgentConnection(client, new MemorySecretStore(AgentHost.TokenA), TimeProvider.System,
            NullLogger<RemoteAgentConnection>.Instance);
        _disposables.Add(connection);
        await connection.ConfigureAsync(new RemoteAgentSettings("http://localhost/"));
        var store = new RemoteJobStore(NullLogger<RemoteJobStore>.Instance, Path.Combine(_directory, "remote-jobs.json"));
        return (new RemoteJobExecutor(connection, store, new NoSleepInhibitor(), TimeProvider.System,
            NullLogger<RemoteJobExecutor>.Instance), store);
    }

    private AgentHost Track(AgentHost host)
    {
        _disposables.Add(host);
        return host;
    }

    private sealed class TestServerHttpClientFactory : IHttpClientFactory
    {
        private readonly AgentHost host;

        public TestServerHttpClientFactory(AgentHost host)
        {
            this.host = host;
            // Start the host now: WebApplicationFactory starts it lazily and not thread-safely,
            // and the connection loop and the executor create clients concurrently.
            _ = host.Server;
        }

        public HttpClient CreateClient(string name)
        {
            var client = host.CreateDefaultClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }
    }

    private sealed class MemorySecretStore(string token) : ISecretStore
    {
        public string? Read(string name) => token;
        public void Write(string name, string secret) { }
        public void Delete(string name) { }
    }
}
