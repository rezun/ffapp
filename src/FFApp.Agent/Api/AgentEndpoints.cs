using System.Globalization;
using System.Net.ServerSentEvents;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using FFApp.Agent.Clients;
using FFApp.Agent.Events;
using FFApp.Agent.Hosting;
using FFApp.Agent.Jobs;
using FFApp.Agent.Storage;
using FFApp.Agent.Updates;
using FFApp.Remote.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace FFApp.Agent.Api;

/// <summary>The REST protocol under <c>/api/v1</c>. Every endpoint except health needs a client token.</summary>
public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(Protocol.ApiPrefix);

        api.MapGet("/health", () => Json(new HealthResponse("ok", AgentVersion.Current), RemoteJsonContext.Default.HealthResponse));

        var secured = api.MapGroup("").RequireAuthorization();

        secured.MapGet("/info", GetInfo);
        secured.MapGet("/queue", (ClaimsPrincipal user, AgentJobQueue queue) =>
            Json(queue.QueueStateFor(user.ClientId()), RemoteJsonContext.Default.QueueState));

        secured.MapPost("/sources", RegisterSourceAsync);
        secured.MapPut("/sources/{id}", AppendSourceAsync).WithMetadata(new DisableRequestSizeLimitAttribute());
        secured.MapPost("/sources/{id}/complete", CompleteSourceAsync);

        secured.MapPost("/jobs", CreateJobAsync);
        secured.MapGet("/jobs", (ClaimsPrincipal user, AgentJobQueue queue) =>
            Json([.. queue.List(user.ClientId())], RemoteJsonContext.Default.ImmutableArrayAgentJob));
        secured.MapGet("/jobs/{id}", (string id, ClaimsPrincipal user, AgentJobQueue queue) =>
            Json(queue.Get(user.ClientId(), id), RemoteJsonContext.Default.AgentJob));
        secured.MapGet("/jobs/{id}/log", GetLog);
        secured.MapPost("/jobs/{id}/cancel", (string id, ClaimsPrincipal user, AgentJobQueue queue) =>
            Json(queue.Cancel(user.ClientId(), id), RemoteJsonContext.Default.AgentJob));
        secured.MapPost("/jobs/{id}/pause", (string id, ClaimsPrincipal user, AgentJobQueue queue) =>
            Json(queue.Pause(user.ClientId(), id), RemoteJsonContext.Default.AgentJob));
        secured.MapPost("/jobs/{id}/resume", (string id, ClaimsPrincipal user, AgentJobQueue queue) =>
            Json(queue.Resume(user.ClientId(), id), RemoteJsonContext.Default.AgentJob));
        secured.MapGet("/jobs/{id}/output", GetOutput);
        secured.MapPost("/jobs/{id}/ack", (string id, ClaimsPrincipal user, AgentJobQueue queue) =>
        {
            queue.Acknowledge(user.ClientId(), id);
            return Results.NoContent();
        });
        secured.MapDelete("/jobs/{id}", (string id, ClaimsPrincipal user, AgentJobQueue queue) =>
        {
            queue.Delete(user.ClientId(), id);
            return Results.NoContent();
        });

        secured.MapGet("/events", StreamEvents);

        secured.MapGet("/update", (UpdateService updates) => Json(updates.Status, RemoteJsonContext.Default.UpdateStatus));
        secured.MapPost("/update", async (HttpRequest request, UpdateService updates, CancellationToken ct) =>
        {
            var body = await ReadAsync(request, RemoteJsonContext.Default.UpdateRequest, ct);
            return Results.Json(updates.Request(body.Version), RemoteJsonContext.Default.UpdateStatus,
                statusCode: StatusCodes.Status202Accepted);
        });
    }

    private static IResult GetInfo(
        ClaimsPrincipal user, AgentTools tools, SourceStore sources, AgentJobQueue queue, AgentPaths paths,
        IOptions<AgentOptions> options)
    {
        var status = tools.Status;
        return Json(new AgentInfo(
            AgentVersion.Current,
            Protocol.Version,
            user.ClientName(),
            status?.Ffmpeg.VersionOutput,
            status?.Capabilities,
            status?.IsLibplaceboAvailable ?? false,
            status?.Mkvpropedit.IsValid ?? false,
            FreeBytes(paths.Root),
            options.Value.QuotaBytes,
            sources.ReservedBytes() + queue.OutputBytes(),
            [.. options.Value.OutputRoots.Keys.Order(StringComparer.Ordinal)],
            tools.Warnings()), RemoteJsonContext.Default.AgentInfo);
    }

    private static async Task<IResult> RegisterSourceAsync(
        HttpRequest request, ClaimsPrincipal user, SourceStore sources, AgentJobQueue queue, AgentPaths paths,
        IOptions<AgentOptions> options, CancellationToken ct)
    {
        var body = await ReadAsync(request, RemoteJsonContext.Default.CreateSourceRequest, ct);
        var clientId = user.ClientId();
        if (sources.Find(clientId, body.Fingerprint, body.Size) is null)
            EnsureRoom(body.Size, sources.ReservedBytes() + queue.OutputBytes(), options.Value.QuotaBytes, FreeBytes(paths.Root));
        var record = sources.Register(clientId, body);
        return Json(sources.Status(record), RemoteJsonContext.Default.SourceStatus);
    }

    /// <summary>Rejects an upload before it starts when quota or disk space would not suffice.</summary>
    internal static void EnsureRoom(long size, long used, long quota, long free)
    {
        var quotaLeft = Math.Max(0, quota - used);
        if (size > quotaLeft)
        {
            throw new AgentException(StatusCodes.Status507InsufficientStorage, ErrorCodes.QuotaExceeded,
                $"The source needs {FormatBytes(size)}, but only {FormatBytes(quotaLeft)} of the agent's quota is free.");
        }
        if (size > free)
        {
            throw new AgentException(StatusCodes.Status507InsufficientStorage, ErrorCodes.DiskFull,
                $"The source needs {FormatBytes(size)}, but the server has only {FormatBytes(free)} free.");
        }
    }

    private static async Task<IResult> AppendSourceAsync(
        string id, HttpRequest request, ClaimsPrincipal user, SourceStore sources, CancellationToken ct)
    {
        var range = request.Headers.ContentRange.ToString();
        if (!ContentRangeHeaderValue.TryParse(range, out var parsed) || parsed.From is not { } from || parsed.Unit != "bytes")
            throw AgentException.Invalid("PUT needs a Content-Range header such as \"bytes 0-1048575/52428800\".");
        var status = await sources.AppendAsync(user.ClientId(), id, from, request.Body, ct);
        return Json(status, RemoteJsonContext.Default.SourceStatus);
    }

    private static async Task<IResult> CompleteSourceAsync(
        string id, HttpRequest request, ClaimsPrincipal user, SourceStore sources, CancellationToken ct)
    {
        var body = await ReadAsync(request, RemoteJsonContext.Default.CompleteSourceRequest, ct);
        return Json(await sources.CompleteAsync(user.ClientId(), id, body.Sha256, ct), RemoteJsonContext.Default.SourceStatus);
    }

    private static async Task<IResult> CreateJobAsync(
        HttpRequest request, ClaimsPrincipal user, AgentJobQueue queue, CancellationToken ct)
    {
        var body = await ReadAsync(request, RemoteJsonContext.Default.CreateJobRequest, ct);
        if (body.Job is null || body.MediaInfo is null)
            throw AgentException.Invalid("job and mediaInfo are required.");
        var job = queue.Create(user.ClientId(), body with { MediaInfo = WireNormalization.Normalize(body.MediaInfo) });
        return Results.Json(job, RemoteJsonContext.Default.AgentJob, statusCode: StatusCodes.Status201Created);
    }

    private static IResult GetLog(string id, ClaimsPrincipal user, AgentJobQueue queue, AgentPaths paths)
    {
        var job = queue.GetRecord(user.ClientId(), id);
        var path = paths.JobLog(job.JobId);
        return File.Exists(path)
            ? Results.File(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), "text/plain; charset=utf-8")
            : Results.Text("", "text/plain; charset=utf-8");
    }

    private static IResult GetOutput(string id, ClaimsPrincipal user, AgentJobQueue queue)
    {
        var path = queue.OutputForDownload(user.ClientId(), id);
        var info = new FileInfo(path);
        var tag = new EntityTagHeaderValue(string.Create(CultureInfo.InvariantCulture,
            $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\""));
        return Results.File(path, "application/octet-stream", fileDownloadName: null,
            lastModified: info.LastWriteTimeUtc, entityTag: tag, enableRangeProcessing: true);
    }

    private static IResult StreamEvents(
        ClaimsPrincipal user, EventHub events, AgentJobQueue queue, UpdateService updates, CancellationToken ct)
    {
        var clientId = user.ClientId();
        SseItem<string>[] initial =
        [
            new(JsonSerializer.Serialize(queue.QueueStateFor(clientId), RemoteJsonContext.Default.QueueState), AgentEventTypes.QueueState),
            new(JsonSerializer.Serialize(updates.Status, RemoteJsonContext.Default.UpdateStatus), AgentEventTypes.UpdateState),
        ];
        return TypedResults.ServerSentEvents(events.StreamAsync(clientId, initial, ct));
    }

    // --- Helpers ---

    private static IResult Json<T>(T value, JsonTypeInfo<T> typeInfo) => Results.Json(value, typeInfo);

    private static async Task<T> ReadAsync<T>(HttpRequest request, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(request.Body, typeInfo, ct)
                ?? throw AgentException.Invalid("The request body is empty.");
        }
        catch (JsonException ex)
        {
            throw AgentException.Invalid($"The request body is not valid: {ex.Message}");
        }
    }

    internal static long FreeBytes(string path)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static string FormatBytes(long bytes) =>
        bytes >= 1_000_000_000
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000_000.0:0.#} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000.0:0.#} MB");
}

/// <summary>Turns <see cref="AgentException"/> into an <see cref="ErrorResponse"/>.</summary>
public sealed class AgentExceptionMiddleware(RequestDelegate next, ILogger<AgentExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AgentException ex) when (!context.Response.HasStarted)
        {
            logger.LogInformation("Rejected {Method} {Path}: {Code} {Message}", context.Request.Method,
                context.Request.Path, ex.Code, ex.Message);
            context.Response.StatusCode = ex.StatusCode;
            await context.Response.WriteAsJsonAsync(new ErrorResponse(ex.Code, ex.Message),
                RemoteJsonContext.Default.ErrorResponse);
        }
    }
}
