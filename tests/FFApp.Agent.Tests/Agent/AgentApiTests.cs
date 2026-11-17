using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Security.Cryptography;
using System.Text.Json;

using FFApp.Remote.Contracts;

namespace FFApp.Agent.Tests.Agent;

public sealed class AgentApiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ffapp-agent-tests-{Guid.NewGuid():N}");
    private static readonly byte[] SourceData = RandomNumberGenerator.GetBytes(300_000);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Upload_Encode_Download_Acknowledge_CleansUp()
    {
        await using var host = new AgentHost(_directory, new FakeProcessRunner());
        var client = host.Client(AgentHost.TokenA);

        // Resumable upload: the first chunk arrives, a retry at a stale offset is refused.
        var source = await client.RegisterAsync(SourceData);
        (await client.PutChunkAsync(source.SourceId, SourceData, 0, 100_000)).EnsureSuccessStatusCode();
        var stale = await client.PutChunkAsync(source.SourceId, SourceData, 0, 100_000);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.ReadErrorAsync()).Code.Should().Be(ErrorCodes.OffsetMismatch);
        (await client.RegisterAsync(SourceData)).ReceivedBytes.Should().Be(100_000, "registering again reports the confirmed offset");
        var sourceId = await client.UploadAsync(SourceData);
        sourceId.Should().Be(source.SourceId);

        var job = await client.SubmitAsync(AgentApi.JobRequest(sourceId));
        var done = await client.WaitForStateAsync(job.JobId, AgentJobState.Completed);
        done.OutputSizeBytes.Should().Be(10);
        host.Runner.Commands.Should().Contain(command => command.Arguments.Contains(
            Path.Combine(host.WorkDirectory, "sources", sourceId, "source.mkv")), "the agent encodes its uploaded copy");

        var range = new HttpRequestMessage(HttpMethod.Get, $"jobs/{job.JobId}/output");
        range.Headers.Range = new RangeHeaderValue(4, null);
        var partial = await client.SendAsync(range);
        partial.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await partial.Content.ReadAsByteArrayAsync()).Should().Equal(5, 6, 7, 8, 9, 10);

        (await client.PostAsync($"jobs/{job.JobId}/ack", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"jobs/{job.JobId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        Directory.Exists(Path.Combine(host.WorkDirectory, "sources", sourceId)).Should().BeFalse("no job needs the source anymore");
    }

    [Fact]
    public async Task Requests_WithoutValidToken_AreRejected_ButHealthIsOpen()
    {
        await using var host = new AgentHost(_directory, new FakeProcessRunner());

        (await host.Client(null).GetAsync("health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.Client(null).GetAsync("info")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await host.Client("wrong").GetAsync("jobs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var info = await (await host.Client(AgentHost.TokenA).GetAsync("info")).ReadAsync(RemoteJsonContext.Default.AgentInfo);
        info.ProtocolVersion.Should().Be(Protocol.Version);
        info.ClientName.Should().Be("MacBook");
        info.OutputRoots.Should().Equal("Movies");
        info.Capabilities!.Encoders.Should().Contain("libsvtav1");
    }

    [Fact]
    public async Task Clients_SeeOnlyTheirOwnJobs_AndAnonymousLoad()
    {
        var runner = new FakeProcessRunner { HoldJobs = true };
        await using var host = new AgentHost(_directory, runner);
        var a = host.Client(AgentHost.TokenA);
        var b = host.Client(AgentHost.TokenB);

        var running = await a.SubmitAsync(AgentApi.JobRequest(await a.UploadAsync(SourceData)));
        await runner.Started;
        var waiting = await a.SubmitAsync(AgentApi.JobRequest(running.SourceId, "queue-2"));
        var foreign = await b.SubmitAsync(AgentApi.JobRequest(await b.UploadAsync(SourceData), "queue-1"));

        foreign.SourceId.Should().NotBe(running.SourceId, "deduplication never crosses clients");
        (await b.GetAsync($"jobs/{running.JobId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await b.PostAsync($"jobs/{running.JobId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var bJobs = await (await b.GetAsync("jobs")).ReadAsync(RemoteJsonContext.Default.ImmutableArrayAgentJob);
        bJobs.Select(job => job.JobId).Should().Equal(foreign.JobId);

        var bView = await b.GetJobAsync(foreign.JobId);
        bView.QueuePosition.Should().Be(2);
        bView.JobsAhead.Should().Be(2, "the running job and the other client's waiting job are ahead");
        var load = await (await b.GetAsync("queue")).ReadAsync(RemoteJsonContext.Default.QueueState);
        load.Running!.Own.Should().BeFalse();
        load.WaitingJobs.Should().Be(2);
        load.OwnWaitingJobs.Should().Be(1);
        (await a.GetJobAsync(waiting.JobId)).QueuePosition.Should().Be(1);

        runner.Release();
        await b.WaitForStateAsync(foreign.JobId, AgentJobState.Completed);
    }

    [Fact]
    public async Task CreateJob_IsIdempotentPerClientJobId()
    {
        await using var host = new AgentHost(_directory, new FakeProcessRunner { HoldJobs = true });
        var client = host.Client(AgentHost.TokenA);
        var request = AgentApi.JobRequest(await client.UploadAsync(SourceData));

        var first = await client.SubmitAsync(request);
        var second = await client.SubmitAsync(request);

        second.JobId.Should().Be(first.JobId);
        (await (await client.GetAsync("jobs")).ReadAsync(RemoteJsonContext.Default.ImmutableArrayAgentJob)).Should().HaveCount(1);
        host.Runner.Release();
    }

    [Fact]
    public async Task Complete_WithWrongChecksum_DiscardsUpload()
    {
        await using var host = new AgentHost(_directory, new FakeProcessRunner());
        var client = host.Client(AgentHost.TokenA);
        var source = await client.RegisterAsync(SourceData);
        (await client.PutChunkAsync(source.SourceId, SourceData, 0, SourceData.Length)).EnsureSuccessStatusCode();

        var response = await client.PostJsonAsync($"sources/{source.SourceId}/complete",
            new CompleteSourceRequest(new string('0', 64)), RemoteJsonContext.Default.CompleteSourceRequest);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.ReadErrorAsync()).Code.Should().Be(ErrorCodes.HashMismatch);
        (await client.RegisterAsync(SourceData)).ReceivedBytes.Should().Be(0);
    }

    [Fact]
    public async Task Register_BeyondQuota_IsRejectedWithNumbers()
    {
        await using var host = new AgentHost(_directory, new FakeProcessRunner(), quotaGiB: 0.0001);
        var client = host.Client(AgentHost.TokenA);

        var response = await client.PostJsonAsync("sources",
            new CreateSourceRequest("fingerprint", 41_000_000_000, "big.mkv"), RemoteJsonContext.Default.CreateSourceRequest);

        response.StatusCode.Should().Be(HttpStatusCode.InsufficientStorage);
        var error = await response.ReadErrorAsync();
        error.Code.Should().Be(ErrorCodes.QuotaExceeded);
        error.Message.Should().Contain("41 GB");
    }

    [Fact]
    public async Task RunningJob_CanBePausedResumedAndCanceled()
    {
        var runner = new FakeProcessRunner { HoldJobs = true };
        await using var host = new AgentHost(_directory, runner);
        var client = host.Client(AgentHost.TokenA);
        var job = await client.SubmitAsync(AgentApi.JobRequest(await client.UploadAsync(SourceData)));
        await runner.Started;
        await client.WaitForStateAsync(job.JobId, AgentJobState.Running);

        (await (await client.PostAsync($"jobs/{job.JobId}/pause", null)).ReadAsync(RemoteJsonContext.Default.AgentJob))
            .State.Should().Be(AgentJobState.Paused);
        runner.Release();
        await Task.Delay(100);
        (await client.GetJobAsync(job.JobId)).State.Should().Be(AgentJobState.Paused, "a paused encoder does not finish");
        (await (await client.PostAsync($"jobs/{job.JobId}/resume", null)).ReadAsync(RemoteJsonContext.Default.AgentJob))
            .State.Should().Be(AgentJobState.Running);
        await client.WaitForStateAsync(job.JobId, AgentJobState.Completed);

        runner.Rearm();
        var second = await client.SubmitAsync(AgentApi.JobRequest(job.SourceId, "queue-2"));
        await client.WaitForStateAsync(second.JobId, AgentJobState.Running);
        (await client.PostAsync($"jobs/{second.JobId}/cancel", null)).EnsureSuccessStatusCode();
        await client.WaitForStateAsync(second.JobId, AgentJobState.Canceled);
    }

    [Fact]
    public async Task AgentRestart_FailsRunningJob_AndKeepsSourceForRunAgain()
    {
        var runner = new FakeProcessRunner { HoldJobs = true };
        string sourceId, jobId;
        await using (var host = new AgentHost(_directory, runner))
        {
            var client = host.Client(AgentHost.TokenA);
            sourceId = await client.UploadAsync(SourceData);
            jobId = (await client.SubmitAsync(AgentApi.JobRequest(sourceId))).JobId;
            await runner.Started;
        }

        await using var restarted = new AgentHost(_directory, new FakeProcessRunner());
        var again = restarted.Client(AgentHost.TokenA);
        var failed = await again.GetJobAsync(jobId);
        failed.State.Should().Be(AgentJobState.Failed);
        failed.FailureMessage.Should().Contain("restarted");

        (await again.RegisterAsync(SourceData)).Should().Match<SourceStatus>(s => s.SourceId == sourceId && s.Complete);
        var rerun = await again.SubmitAsync(AgentApi.JobRequest(sourceId, "queue-1-retry"));
        (await again.DeleteAsync($"jobs/{jobId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await again.WaitForStateAsync(rerun.JobId, AgentJobState.Completed);
    }

    [Fact]
    public async Task KeepOnServer_WritesIntoOutputRoot_WithoutOverwriting()
    {
        await using var host = new AgentHost(_directory, new FakeProcessRunner());
        var client = host.Client(AgentHost.TokenA);
        File.WriteAllText(Path.Combine(host.OutputRoot, "movie - AV1.mkv"), "existing");
        var sourceId = await client.UploadAsync(SourceData);

        var job = await client.SubmitAsync(AgentApi.JobRequest(sourceId, mode: ResultMode.KeepOnServer, root: "Movies"));
        var done = await client.WaitForStateAsync(job.JobId, AgentJobState.Completed);

        done.ServerOutputPath.Should().Be(Path.Combine(host.OutputRoot, "movie - AV1-1.mkv"));
        File.ReadAllText(Path.Combine(host.OutputRoot, "movie - AV1.mkv")).Should().Be("existing");
        (await client.DeleteAsync($"jobs/{job.JobId}")).EnsureSuccessStatusCode();
        File.Exists(done.ServerOutputPath).Should().BeTrue("results kept on the server stay after the job is removed");
    }

    [Theory]
    [InlineData("../escape.mkv", null)]
    [InlineData("sub/dir.mkv", null)]
    [InlineData("movie.mkv", "Unknown")]
    public async Task CreateJob_RejectsPathsOutsideTheAllowlist(string fileName, string? root)
    {
        await using var host = new AgentHost(_directory, new FakeProcessRunner());
        var client = host.Client(AgentHost.TokenA);
        var request = AgentApi.JobRequest(await client.UploadAsync(SourceData), outputFileName: fileName,
            mode: root is null ? ResultMode.Download : ResultMode.KeepOnServer, root: root);

        var response = await client.PostJsonAsync("jobs", request, RemoteJsonContext.Default.CreateJobRequest);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Events_ReportJobStateToItsOwner()
    {
        await using var host = new AgentHost(_directory, new FakeProcessRunner());
        var client = host.Client(AgentHost.TokenA);
        var sourceId = await client.UploadAsync(SourceData);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync("events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var events = SseParser.Create(stream).EnumerateAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.EventType.Should().Be(AgentEventTypes.QueueState, "a new connection starts with the current load");
        var job = await client.SubmitAsync(AgentApi.JobRequest(sourceId));

        while (await events.MoveNextAsync())
        {
            if (events.Current.EventType != AgentEventTypes.JobState)
                continue;
            var state = JsonSerializer.Deserialize(events.Current.Data, RemoteJsonContext.Default.AgentJob)!;
            state.JobId.Should().Be(job.JobId);
            if (state.State == AgentJobState.Completed)
                return;
        }
        throw new InvalidOperationException("The event stream ended before the job completed.");
    }

    [Fact]
    public async Task FailedEncode_ReportsReason()
    {
        await using var host = new AgentHost(_directory, new FakeProcessRunner { ExitCode = 1 });
        var client = host.Client(AgentHost.TokenA);

        var job = await client.SubmitAsync(AgentApi.JobRequest(await client.UploadAsync(SourceData)));
        var failed = await client.WaitForStateAsync(job.JobId, AgentJobState.Failed);

        failed.FailureMessage.Should().Contain("exited with code 1").And.Contain("out of cheese");
        (await client.GetStringAsync($"jobs/{job.JobId}/log")).Should().Contain("out of cheese");
    }
}
