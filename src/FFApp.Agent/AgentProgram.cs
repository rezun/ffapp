using FFApp.Agent.Api;
using FFApp.Agent.Clients;
using FFApp.Agent.Events;
using FFApp.Agent.Hosting;
using FFApp.Agent.Jobs;
using FFApp.Agent.Storage;
using FFApp.Agent.Updates;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;
using FFApp.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FFApp.Agent;

/// <summary>
/// Command-line entry: <c>serve</c> (default), <c>check</c>, <c>startup-check</c>,
/// <c>client add|list|remove</c> and <c>version</c>.
/// </summary>
public static class AgentProgram
{
    public static readonly string ConfigEnvironmentVariable = "FFAPP_AGENT_CONFIG";

    private static readonly string Usage = """
        Usage: ffapp-agent [command]

        Commands:
          serve                  Run the agent (default)
          check                  Check FFmpeg, mkvpropedit and the configuration
          startup-check          Run by systemd before each start; rolls back failed updates
          client add <name>      Add a client and print its token once
          client list            List clients
          client remove <name>   Remove a client, its jobs, sources and results
          version                Print the agent version

        Configuration: /etc/ffapp-agent/appsettings.json, or the file named by FFAPP_AGENT_CONFIG.
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "serve";
        var rest = args.Length > 0 && !args[0].StartsWith('-') ? args[1..] : args;

        switch (command)
        {
            case "serve":
                await CreateApp(rest, quiet: false).RunAsync();
                return 0;
            case "version":
                Console.WriteLine(AgentVersion.Current);
                return 0;
            case "startup-check":
                return StartupCheck.Run(CreateApp([], quiet: true).Services.GetRequiredService<AgentPaths>(), Console.Out);
            case "check":
                return await CheckAsync(CreateApp([], quiet: true).Services);
            case "output-roots":
                // Used by ffapp-agent-install.sh to write ReadWritePaths into the systemd unit.
                foreach (var path in CreateApp([], quiet: true).Services.GetRequiredService<IOptions<AgentOptions>>().Value.OutputRoots.Values)
                    Console.WriteLine(path);
                return 0;
            case "client":
                return RunClientCommand(CreateApp([], quiet: true).Services, rest);
            case "help" or "--help" or "-h":
                Console.WriteLine(Usage);
                return 0;
            default:
                Console.Error.WriteLine($"Unknown command: {command}");
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }

    public static WebApplication CreateApp(string[] args, bool quiet)
    {
        var builder = WebApplication.CreateBuilder(args);
        var configPath = Environment.GetEnvironmentVariable(ConfigEnvironmentVariable)
            ?? Path.Combine(AgentOptions.DefaultConfigDirectory, "appsettings.json");
        builder.Configuration.AddJsonFile(configPath, optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables("FFAPP_AGENT_");
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        if (quiet)
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Host.UseSystemd();
        builder.WebHost.UseUrls(builder.Configuration[$"{AgentOptions.SectionName}:{nameof(AgentOptions.Urls)}"]
            ?? new AgentOptions().Urls);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MinRequestBodyDataRate = null);

        ConfigureServices(builder.Services, builder.Configuration);

        var app = builder.Build();
        app.UseMiddleware<AgentExceptionMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAgentEndpoints();
        return app;
    }

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentOptions>().Bind(configuration.GetSection(AgentOptions.SectionName));
        services.AddSingleton(TimeProvider.System);

        services.AddAuthentication(ClientTokenAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ClientTokenAuthenticationHandler>(ClientTokenAuthenticationHandler.SchemeName, null);
        services.AddAuthorization();
        services.AddHttpClient(UpdateService.HttpClientName, client =>
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"ffapp-agent/{AgentVersion.Current}"));

        // Encoding, shared with the desktop app.
        services.AddSingleton<ExecutablePathsConfig>();
        services.AddSingleton<TranscodingPreferences>();
        services.AddSingleton<IDependencyService, DependencyManager>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<ICommandBuilder, DefaultCommandBuilder>();
        services.AddSingleton<IMatroskaPostProcessor, MatroskaPostProcessor>();
        services.AddSingleton<IFfmpegCapabilityService, FfmpegCapabilityService>();
        services.AddSingleton<IJobExecutionService, DefaultJobExecutionService>();

        // Agent state.
        services.AddSingleton<AgentPaths>();
        services.AddSingleton<AgentTools>();
        services.AddSingleton<ClientStore>();
        services.AddSingleton<ClientRegistry>();
        services.AddSingleton<SourceStore>();
        services.AddSingleton<ThroughputHistory>();
        services.AddSingleton<EventHub>();
        services.AddSingleton<AgentJobQueue>();
        services.AddSingleton<UpdateService>();

        services.AddHostedService<AgentStartup>();
        services.AddHostedService<JobWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
        services.AddHostedService<MaintenanceService>();
    }

    private static int RunClientCommand(IServiceProvider services, string[] args)
    {
        var store = services.GetRequiredService<ClientStore>();
        try
        {
            switch (args)
            {
                case ["add", var name]:
                    var (client, token) = store.Add(name);
                    Console.WriteLine($"Added client \"{client.Name}\". Its token is shown only once:");
                    Console.WriteLine();
                    Console.WriteLine($"  {token}");
                    Console.WriteLine();
                    Console.WriteLine("Enter it in FFApp under Settings → Server agent.");
                    return 0;
                case ["list"]:
                    var clients = store.Load();
                    if (clients.Length == 0)
                        Console.WriteLine("No clients. Add one with: ffapp-agent client add <name>");
                    foreach (var entry in clients)
                        Console.WriteLine($"{entry.Name}\tadded {entry.CreatedAt:yyyy-MM-dd}");
                    return 0;
                case ["remove", var name]:
                    if (!store.Remove(name))
                    {
                        Console.Error.WriteLine($"No client named \"{name}\".");
                        return 1;
                    }
                    Console.WriteLine($"Removed client \"{name}\". The agent deletes its jobs, sources and results shortly.");
                    return 0;
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> CheckAsync(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<AgentOptions>>().Value;
        var tools = services.GetRequiredService<AgentTools>();
        var status = await tools.DiscoverAsync(CancellationToken.None);

        Console.WriteLine($"FFApp agent {AgentVersion.Current} ({AgentVersion.RuntimeIdentifier})");
        Report("ffmpeg", status.Ffmpeg);
        Report("ffprobe", status.Ffprobe);
        Report("mkvpropedit", status.Mkvpropedit, required: false);

        if (status.Capabilities is { } capabilities)
        {
            var encoders = Enum.GetValues<VideoCodec>().Select(DefaultCommandBuilder.VideoEncoderName)
                .Concat(Enum.GetValues<AudioCodec>().Select(DefaultCommandBuilder.AudioEncoderName));
            foreach (var encoder in encoders)
                Console.WriteLine($"  {(capabilities.Encoders.Contains(encoder) ? "ok     " : "missing")} encoder {encoder}");
            Console.WriteLine($"  {(status.IsLibplaceboAvailable ? "ok     " : "missing")} filter libplacebo (optional HDR tone mapping)");
        }

        Console.WriteLine($"Work directory: {options.WorkDirectory}");
        Console.WriteLine($"Clients file:   {options.ClientsFile}");
        foreach (var (name, path) in options.OutputRoots)
            Console.WriteLine($"Output folder \"{name}\": {path}{(Directory.Exists(path) ? "" : " (missing)")}");
        foreach (var warning in tools.Warnings())
            Console.WriteLine($"warning: {warning}");
        if (!status.IsReady)
            Console.WriteLine($"Install FFmpeg {DependencyManager.MinimumFfmpegMajorVersion} or newer, for example with: ffapp-agent-install.sh --with-ffmpeg");
        return status.IsReady ? 0 : 1;

        static void Report(string name, ExecutableValidation tool, bool required = true)
        {
            var state = tool.IsValid ? "ok     " : required ? "ERROR  " : "missing";
            Console.WriteLine($"{state} {name}: {(tool.IsValid ? tool.VersionOutput : tool.Error)} {tool.Path}");
        }
    }
}
