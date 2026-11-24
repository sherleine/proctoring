using Microsoft.Extensions.Options;
using Proctoring.Application.Events;

namespace Proctoring.Api.Background;

/// <summary>Runs <see cref="EventMaintenance"/> every EVENT_MAINTENANCE_INTERVAL_SECONDS (and once at startup).</summary>
public sealed class EventMaintenanceHostedService(IServiceScopeFactory scopes, IOptions<AudioEventOptions> options,
    IOptions<LiveUpdateOptions> live, TimeProvider clock, ILogger<EventMaintenanceHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.MaintenanceEnabled)
        {
            logger.LogWarning("Event maintenance is disabled (EVENT_MAINTENANCE_ENABLED=false): failed publishes are not retried automatically");
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.MaintenanceIntervalSeconds));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<EventMaintenance>().RunOnceAsync(stoppingToken);
                // Live-update change log: entries older than EVENT_UPDATES_RETENTION_HOURS. A client reconnecting
                // with an older Last-Event-ID is told to re-sync from GET /events.
                var cutoff = clock.GetUtcNow().UtcDateTime.AddHours(-live.Value.RetentionHours);
                var pruned = await scope.ServiceProvider.GetRequiredService<IEventUpdateRepository>().DeleteOlderThanAsync(cutoff, stoppingToken);
                if (pruned > 0) logger.LogInformation("Pruned {Count} live-update entries", pruned);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Event maintenance run failed; it will run again in {Seconds} s", options.Value.MaintenanceIntervalSeconds);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
