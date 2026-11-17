using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using FFApp.Remote.Contracts;

namespace FFApp.Remote;

/// <summary>Address and token of the server agent.</summary>
public sealed record AgentEndpoint(Uri BaseUrl, string Token)
{
    /// <summary>Accepts "host", "host:port" or a full URL; adds http:// and the default port.</summary>
    public static Uri? ParseBaseUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var value = text.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        var builder = new UriBuilder(uri) { Path = "/", Query = "", Fragment = "" };
        if (!text.Contains("://", StringComparison.Ordinal) && uri.IsDefaultPort)
            builder.Port = Protocol.DefaultPort;
        return builder.Uri;
    }
}

/// <summary>An error the agent reported, or a failure to reach it.</summary>
public sealed class RemoteAgentException(string message, HttpStatusCode? statusCode = null, string? code = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public string? Code { get; } = code;

    /// <summary>True for network failures and server errors worth retrying.</summary>
    public bool IsTransient => StatusCode is null or >= HttpStatusCode.InternalServerError and not HttpStatusCode.InsufficientStorage;
}

/// <summary>Typed access to the agent's REST protocol. Every call takes the endpoint to use.</summary>
public sealed class RemoteAgentClient(IHttpClientFactory httpClientFactory)
{
    public static readonly string HttpClientName = "ffapp-agent";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public Task<AgentInfo> GetInfoAsync(AgentEndpoint endpoint, CancellationToken ct) =>
        SendAsync(endpoint, HttpMethod.Get, "info", null, RemoteJsonContext.Default.AgentInfo, ct);

    public Task<QueueState> GetQueueAsync(AgentEndpoint endpoint, CancellationToken ct) =>
        SendAsync(endpoint, HttpMethod.Get, "queue", null, RemoteJsonContext.Default.QueueState, ct);

    public Task<SourceStatus> RegisterSourceAsync(AgentEndpoint endpoint, CreateSourceRequest request, CancellationToken ct) =>
        SendAsync(endpoint, HttpMethod.Post, "sources", JsonContent.Create(request, RemoteJsonContext.Default.CreateSourceRequest),
            RemoteJsonContext.Default.SourceStatus, ct);

    /// <summary>Sends <paramref name="count"/> bytes of <paramref name="content"/> starting at <paramref name="offset"/>.</summary>
    public async Task<SourceStatus> UploadChunkAsync(
        AgentEndpoint endpoint, string sourceId, Stream content, long offset, long count, long total, CancellationToken ct)
    {
        var body = new StreamContent(content);
        body.Headers.ContentLength = count;
        body.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, total);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await SendAsync(endpoint, HttpMethod.Put, $"sources/{Uri.EscapeDataString(sourceId)}", body,
            RemoteJsonContext.Default.SourceStatus, ct, timeout: Timeout.InfiniteTimeSpan);
    }

    public Task<SourceStatus> CompleteSourceAsync(AgentEndpoint endpoint, string sourceId, string sha256, CancellationToken ct) =>
        SendAsync(endpoint, HttpMethod.Post, $"sources/{Uri.EscapeDataString(sourceId)}/complete",
            JsonContent.Create(new CompleteSourceRequest(sha256), RemoteJsonContext.Default.CompleteSourceRequest),
            RemoteJsonContext.Default.SourceStatus, ct, timeout: TimeSpan.FromMinutes(30));

    public Task<AgentJob> CreateJobAsync(AgentEndpoint endpoint, CreateJobRequest request, CancellationToken ct) =>
        SendAsync(endpoint, HttpMethod.Post, "jobs", JsonContent.Create(request, RemoteJsonContext.Default.CreateJobRequest),
            RemoteJsonContext.Default.AgentJob, ct);

    public Task<System.Collections.Immutable.ImmutableArray<AgentJob>> GetJobsAsync(AgentEndpoint endpoint, CancellationToken ct) =>
        SendAsync(endpoint, HttpMethod.Get, "jobs", null, RemoteJsonContext.Default.ImmutableArrayAgentJob, ct);

    public Task<AgentJob> GetJobAsync(AgentEndpoint endpoint, string jobId, CancellationToken ct) =>
        SendAsync(endpoint, HttpMethod.Get, JobPath(jobId), null, RemoteJsonContext.Default.AgentJob, ct);

    public Task<AgentJob> CancelJobAsync(AgentEndpoint endpoint, string jobId, CancellationToken ct) =>
        SendAsync(endpoint, HttpMethod.Post, JobPath(jobId) + "/cancel", null, RemoteJsonContext.Default.AgentJob, ct);

    public Task<AgentJob> PauseJobAsync(AgentEndpoint endpoint, string jobId, CancellationToken ct) =>
        SendAsync(endpoint, HttpMethod.Post, JobPath(jobId) + "/pause", null, RemoteJsonContext.Default.AgentJob, ct);

    public Task<AgentJob> ResumeJobAsync(AgentEndpoint endpoint, string jobId, CancellationToken ct) =>
        SendAsync(endpoint, HttpMethod.Post, JobPath(jobId) + "/resume", null, RemoteJsonContext.Default.AgentJob, ct);

    public Task AcknowledgeAsync(AgentEndpoint endpoint, string jobId, CancellationToken ct) =>
        SendAsync(endpoint, HttpMethod.Post, JobPath(jobId) + "/ack", null, ct);

    public Task DeleteJobAsync(AgentEndpoint endpoint, string jobId, CancellationToken ct) =>
        SendAsync(endpoint, HttpMethod.Delete, JobPath(jobId), null, ct);

    public async Task<string> GetLogAsync(AgentEndpoint endpoint, string jobId, CancellationToken ct)
    {
        using var response = await SendRawAsync(endpoint, HttpMethod.Get, JobPath(jobId) + "/log", null,
            HttpCompletionOption.ResponseContentRead, RequestTimeout, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Opens the result for download from <paramref name="offset"/>. The caller disposes the
    /// response. A 200 instead of 206 means the agent sent the whole file.
    /// </summary>
    public Task<HttpResponseMessage> OpenOutputAsync(AgentEndpoint endpoint, string jobId, long offset, CancellationToken ct)
    {
        var request = CreateRequest(endpoint, HttpMethod.Get, JobPath(jobId) + "/output", null);
        if (offset > 0)
            request.Headers.Range = new RangeHeaderValue(offset, null);
        return SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead, Timeout.InfiniteTimeSpan, ct);
    }

    public Task<UpdateStatus> GetUpdateAsync(AgentEndpoint endpoint, CancellationToken ct) =>
        SendAsync(endpoint, HttpMethod.Get, "update", null, RemoteJsonContext.Default.UpdateStatus, ct);

    public Task<UpdateStatus> RequestUpdateAsync(AgentEndpoint endpoint, string version, CancellationToken ct) =>
        SendAsync(endpoint, HttpMethod.Post, "update", JsonContent.Create(new UpdateRequest(version), RemoteJsonContext.Default.UpdateRequest),
            RemoteJsonContext.Default.UpdateStatus, ct);

    /// <summary>Streams server-sent events until the connection ends or <paramref name="ct"/> is canceled.</summary>
    public async IAsyncEnumerable<SseItem<string>> StreamEventsAsync(AgentEndpoint endpoint, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendRawAsync(endpoint, HttpMethod.Get, "events", null,
            HttpCompletionOption.ResponseHeadersRead, Timeout.InfiniteTimeSpan, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(ct))
            yield return item;
    }

    // --- Plumbing ---

    private static string JobPath(string jobId) => "jobs/" + Uri.EscapeDataString(jobId);

    private async Task<T> SendAsync<T>(AgentEndpoint endpoint, HttpMethod method, string path, HttpContent? content,
        JsonTypeInfo<T> typeInfo, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var response = await SendRawAsync(endpoint, method, path, content, HttpCompletionOption.ResponseContentRead,
            timeout ?? RequestTimeout, ct);
        try
        {
            return await response.Content.ReadFromJsonAsync(typeInfo, ct)
                ?? throw new RemoteAgentException("The server agent sent an empty response.", response.StatusCode);
        }
        catch (JsonException ex)
        {
            throw new RemoteAgentException("The server agent sent a response this app does not understand. Update the agent.",
                response.StatusCode, inner: ex);
        }
    }

    private async Task SendAsync(AgentEndpoint endpoint, HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var _ = await SendRawAsync(endpoint, method, path, content, HttpCompletionOption.ResponseContentRead, RequestTimeout, ct);
    }

    private Task<HttpResponseMessage> SendRawAsync(AgentEndpoint endpoint, HttpMethod method, string path, HttpContent? content,
        HttpCompletionOption completion, TimeSpan timeout, CancellationToken ct) =>
        SendRequestAsync(CreateRequest(endpoint, method, path, content), completion, timeout, ct);

    private static HttpRequestMessage CreateRequest(AgentEndpoint endpoint, HttpMethod method, string path, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, new Uri(endpoint.BaseUrl, Protocol.ApiPrefix.TrimStart('/') + "/" + path))
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        return request;
    }

    /// <summary>Sends a request and turns failures into <see cref="RemoteAgentException"/>.</summary>
    private async Task<HttpResponseMessage> SendRequestAsync(
        HttpRequestMessage request, HttpCompletionOption completion, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan)
            timeoutCts.CancelAfter(timeout);
        var client = httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, completion, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RemoteAgentException("The server agent did not answer in time.", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteAgentException($"The server agent is not reachable: {ex.Message}", inner: ex);
        }
        finally
        {
            request.Dispose();
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            ErrorResponse? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync(RemoteJsonContext.Default.ErrorResponse, ct);
            }
            catch (JsonException)
            {
                // Not an agent error body, for example a proxy page; fall back to the status code.
            }
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "The server agent rejected the token. Check Settings → Server agent.",
                _ when error is not null => error.Message,
                _ => $"The server agent answered {(int)response.StatusCode} {response.ReasonPhrase}.",
            };
            throw new RemoteAgentException(message, response.StatusCode, error?.Code);
        }
    }
}
