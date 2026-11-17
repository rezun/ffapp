using FFApp.Agent.Clients;
using FFApp.Agent.Jobs;

namespace FFApp.Agent.Hosting;

/// <summary>
/// Removes the data of clients deleted from <c>clients.json</c> and applies the retention period.
/// </summary>
public sealed class MaintenanceService(
    AgentJobQueue queue,
    ClientRegistry clients,
    TimeProvider timeProvider,
    ILogger<MaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan ClientCheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ClientCheckInterval, timeProvider);
        var lastRetention = long.MinValue;
        do
        {
            try
            {
                clients.Refresh();
                if (clients.HasClientFile)
                    queue.PurgeClients(clients.Clients.Select(c => c.ClientId).ToHashSet(StringComparer.Ordinal));
                if (lastRetention == long.MinValue || timeProvider.GetElapsedTime(lastRetention) >= RetentionInterval)
                {
                    lastRetention = timeProvider.GetTimestamp();
                    queue.ApplyRetention();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Maintenance failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
