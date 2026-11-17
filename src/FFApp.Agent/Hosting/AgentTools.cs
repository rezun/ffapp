using System.Collections.Immutable;

using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using Microsoft.Extensions.Options;

namespace FFApp.Agent.Hosting;

/// <summary>The agent's FFmpeg installation as found at startup.</summary>
public sealed class AgentTools(
    IDependencyService dependencies,
    ExecutablePathsConfig pathsConfig,
    IFfmpegCapabilityService capabilities,
    IOptions<AgentOptions> options,
    ILogger<AgentTools> logger)
{
    public DependencyStatus? Status { get; private set; }

    public string? FfmpegVersion => Status?.Ffmpeg.VersionOutput;

    public async Task<DependencyStatus> DiscoverAsync(CancellationToken ct)
    {
        var tools = options.Value.Tools;
        var status = await dependencies.DiscoverAsync(new ExecutablePaths(
            NullIfEmpty(tools.Ffmpeg) ?? Bundled("ffmpeg"),
            NullIfEmpty(tools.Ffprobe) ?? Bundled("ffprobe"),
            NullIfEmpty(tools.Mkvpropedit)), ct);
        pathsConfig.Configure(status.ExecutablePaths);
        capabilities.Capabilities = status.Capabilities;
        capabilities.IsLibplaceboAvailable = status.IsLibplaceboAvailable;
        Status = status;

        if (status.IsReady)
            logger.LogInformation("Using {Ffmpeg}", status.Ffmpeg.VersionOutput);
        else
            logger.LogError("FFmpeg is not usable: {Error}", status.RequiredToolsError);
        return status;
    }

    /// <summary>Problems an administrator must fix; shown by <c>ffapp-agent check</c> and in the app.</summary>
    public ImmutableArray<string> Warnings()
    {
        var warnings = ImmutableArray.CreateBuilder<string>();
        if (Status is { } status)
        {
            if (!status.IsReady)
                warnings.Add(status.RequiredToolsError);
            if (!status.Mkvpropedit.IsValid)
                warnings.Add("mkvpropedit was not found. Matroska statistics cannot be recalculated on the server.");
        }
        foreach (var (name, path) in options.Value.OutputRoots)
        {
            if (!Directory.Exists(path))
                warnings.Add($"The output folder \"{name}\" ({path}) does not exist.");
        }
        return warnings.ToImmutable();
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The static FFmpeg build that <c>install.sh --with-ffmpeg</c> puts into <c>tools/</c>.</summary>
    private string? Bundled(string name)
    {
        var path = Path.Combine(options.Value.WorkDirectory, "tools", name);
        return File.Exists(path) ? path : null;
    }
}

/// <summary>Finds FFmpeg and loads persisted state before the agent accepts requests.</summary>
public sealed class AgentStartup(
    AgentTools tools,
    Storage.SourceStore sources,
    Jobs.AgentJobQueue queue,
    Jobs.ThroughputHistory throughput,
    Clients.ClientRegistry clients) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        sources.Load();
        throughput.Load();
        queue.Load();
        clients.Refresh(force: true);
        await tools.DiscoverAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
