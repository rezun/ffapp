using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using FFApp.Agent.Clients;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Remote.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FFApp.Agent.Tests.Agent;

/// <summary>
/// Hosts the agent in memory with a temporary work directory, two clients and a fake encoder.
/// A second instance on the same directory simulates an agent restart.
/// </summary>
public sealed class AgentHost : WebApplicationFactory<global::Program>
{
    public static readonly string TokenA = "token-client-a";
    public static readonly string TokenB = "token-client-b";

    public AgentHost(string directory, FakeProcessRunner runner, double quotaGiB = 1)
    {
        Directory = directory;
        Runner = runner;
        QuotaGiB = quotaGiB;
        System.IO.Directory.CreateDirectory(directory);
        var clients = new ClientList(
        [
            new AgentClient("client-a", "MacBook", ClientTokens.Hash(TokenA), DateTimeOffset.UnixEpoch),
            new AgentClient("client-b", "Windows PC", ClientTokens.Hash(TokenB), DateTimeOffset.UnixEpoch),
        ]);
        File.WriteAllText(ClientsFile, JsonSerializer.Serialize(clients, AgentJsonContext.Default.ClientList));
        System.IO.Directory.CreateDirectory(OutputRoot);
    }

    public string Directory { get; }
    public FakeProcessRunner Runner { get; }
    public double QuotaGiB { get; }
    public string WorkDirectory => Path.Combine(Directory, "work");
    public string ClientsFile => Path.Combine(Directory, "clients.json");
    public string OutputRoot => Path.Combine(Directory, "movies");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:WorkDirectory"] = WorkDirectory,
            ["Agent:ClientsFile"] = ClientsFile,
            ["Agent:QuotaGiB"] = QuotaGiB.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Agent:OutputRoots:Movies"] = OutputRoot,
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IProcessRunner>();
            services.AddSingleton<IProcessRunner>(Runner);
            services.RemoveAll<IDependencyService>();
            services.AddSingleton<IDependencyService, FakeDependencyService>();
        });
    }

    public HttpClient Client(string? token)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost/api/v1/") });
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

internal sealed class FakeDependencyService : IDependencyService
{
    private static readonly DependencyStatus Ready = new(
        new ExecutableValidation("ffmpeg", "/usr/bin/ffmpeg", true, "ffmpeg version 9.0"),
        new ExecutableValidation("ffprobe", "/usr/bin/ffprobe", true, "ffprobe version 9.0"),
        new ExecutableValidation("mkvpropedit", "/usr/bin/mkvpropedit", true, "mkvpropedit v90"),
        false,
        new FfmpegCapabilities(
            ImmutableHashSet.Create("libx264", "libx265", "libvpx-vp9", "libsvtav1", "aac", "libopus", "libmp3lame", "libvorbis"),
            ImmutableHashSet.Create("scale", "fps", "crop", "format", "zscale", "tonemap")));

    public Task<DependencyStatus> DiscoverAsync(ExecutablePaths configured, CancellationToken ct = default) => Task.FromResult(Ready);

    public Task<DependencyStatus> ValidatePathsAsync(ExecutablePaths paths, CancellationToken ct = default) => Task.FromResult(Ready);
}

/// <summary>Helpers that speak the protocol the way the app does.</summary>
internal static class AgentApi
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"the agent answered {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize(body, typeInfo)!;
    }

    public static async Task<ErrorResponse> ReadErrorAsync(this HttpResponseMessage response) =>
        JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), RemoteJsonContext.Default.ErrorResponse)!;

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string path, T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        client.PostAsync(path, JsonContent.Create(value, typeInfo));

    public static async Task<SourceStatus> RegisterAsync(this HttpClient client, byte[] data, string name = "movie.mkv") =>
        await (await client.PostJsonAsync("sources",
            new CreateSourceRequest(Convert.ToHexStringLower(SHA256.HashData(data)), data.Length, name),
            RemoteJsonContext.Default.CreateSourceRequest)).ReadAsync(RemoteJsonContext.Default.SourceStatus);

    public static Task<HttpResponseMessage> PutChunkAsync(this HttpClient client, string sourceId, byte[] data, int offset, int count)
    {
        var content = new ByteArrayContent(data, offset, count);
        content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, data.Length);
        return client.PutAsync($"sources/{sourceId}", content);
    }

    public static async Task<string> UploadAsync(this HttpClient client, byte[] data, string name = "movie.mkv")
    {
        var source = await client.RegisterAsync(data, name);
        if (!source.Complete)
        {
            (await client.PutChunkAsync(source.SourceId, data, (int)source.ReceivedBytes, data.Length - (int)source.ReceivedBytes))
                .EnsureSuccessStatusCode();
            (await client.PostJsonAsync($"sources/{source.SourceId}/complete",
                new CompleteSourceRequest(Convert.ToHexStringLower(SHA256.HashData(data))),
                RemoteJsonContext.Default.CompleteSourceRequest)).EnsureSuccessStatusCode();
        }
        return source.SourceId;
    }

    public static CreateJobRequest JobRequest(string sourceId, string clientJobId = "queue-1",
        ResultMode mode = ResultMode.Download, string outputFileName = "movie - AV1.mkv", string? root = null) =>
        new(clientJobId, sourceId,
            new JobDefinition("/Users/me/movie.mkv", "/Users/me/movie - AV1.mkv", OutputContainer.Mkv,
                VideoCodec: VideoCodec.H264, AudioOptions: new AudioOptions(AudioMode.Copy),
                VideoEncoderOptions: H26xEncoderOptions.ForCodec(VideoCodec.H264),
                TargetQuality: H26xQualityOptions.ForCodec(VideoCodec.H264)),
            new SourceMediaInfo
            {
                SourcePath = "/Users/me/movie.mkv",
                DurationSeconds = 60,
                VideoStreams = [new VideoStreamInfo(0, "h264", Width: 1920, Height: 1080, FrameRate: 25)],
                AudioStreams = [new AudioStreamInfo(1, "aac", Channels: 2)],
            },
            mode, outputFileName, root);

    public static async Task<AgentJob> SubmitAsync(this HttpClient client, CreateJobRequest request) =>
        await (await client.PostJsonAsync("jobs", request, RemoteJsonContext.Default.CreateJobRequest))
            .ReadAsync(RemoteJsonContext.Default.AgentJob);

    public static async Task<AgentJob> GetJobAsync(this HttpClient client, string jobId) =>
        await (await client.GetAsync($"jobs/{jobId}")).ReadAsync(RemoteJsonContext.Default.AgentJob);

    public static async Task<AgentJob> WaitForStateAsync(this HttpClient client, string jobId, params AgentJobState[] states)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            var job = await client.GetJobAsync(jobId);
            if (states.Contains(job.State))
                return job;
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Job {jobId} stayed {job.State}, expected {string.Join(", ", states)}.");
            await Task.Delay(20);
        }
    }
}
