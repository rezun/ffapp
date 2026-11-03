using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace FFApp.Infrastructure.Logging;

/// <summary>
/// The app log: one rolling file per day in <see cref="AppPaths.LogDirectory"/>. Job output does not go
/// here; every job gets its own file from <see cref="JobLogStore"/>.
/// </summary>
public static class AppLogging
{
    public static readonly string AppLogFilePattern = "app-.log";
    public static readonly string JobLogDirectoryName = "jobs";

    private static readonly int RetainedAppLogFiles = 14;
    private static readonly long AppLogFileSizeLimitBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan JobLogRetention = TimeSpan.FromDays(30);

    private static readonly string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Creates the file logger. The caller owns it and disposes it after the app has shut down, so the last
    /// lines are flushed. Writing problems never throw; Serilog drops the events instead.
    /// </summary>
    public static Serilog.Core.Logger CreateLogger(AppIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var path = Path.Combine(AppPaths.LogDirectory(identity), AppLogFilePattern);

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("SourceContext", identity.Name)
            .WriteTo.File(
                path,
                outputTemplate: OutputTemplate,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedAppLogFiles,
                fileSizeLimitBytes: AppLogFileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true)
            .CreateLogger();
    }

    /// <summary>
    /// Registers <see cref="Microsoft.Extensions.Logging.ILogger{T}"/> backed by <paramref name="logger"/>
    /// (or by nothing, in tests and the designer) and the per-job log store.
    /// </summary>
    public static IServiceCollection AddAppLogging(
        this IServiceCollection services, AppIdentity identity, Serilog.ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(identity);

        services.AddLogging(builder =>
        {
            if (logger is not null)
                builder.AddSerilog(logger);
        });
        services.AddSingleton(new JobLogOptions(
            Path.Combine(AppPaths.LogDirectory(identity), JobLogDirectoryName),
            JobLogRetention,
            ApplicationLabel(identity)));
        services.AddSingleton<IJobLogStore, JobLogStore>();
        return services;
    }

    /// <summary>
    /// Writes crashes and unobserved task failures to the log. The crash itself still happens; this only
    /// makes sure there is a record of it.
    /// </summary>
    public static void LogUnhandledExceptions(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        // The file sink is unbuffered, so the event is on disk before the process dies.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.Fatal(e.ExceptionObject as Exception, "Unhandled exception, terminating: {IsTerminating}", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            logger.Error(e.Exception, "Unobserved task exception");
    }

    /// <summary>Logs the start of a session so each launch is easy to find in the file.</summary>
    public static void LogStartup(Serilog.ILogger logger, AppIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(identity);
        logger.Information("{Application} starting on {OS} ({Architecture}), .NET {Runtime}",
            ApplicationLabel(identity),
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version);
    }

    internal static string ApplicationLabel(AppIdentity identity)
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null ? identity.Name : $"{identity.Name} {version.ToString(3)}";
    }
}
