using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RdtClient.Service.Services.TorBoxCleanup;

namespace RdtClient.Service.BackgroundServices;

/// <summary>
///     Hosts the TorBox leftover cleanup. Wakes every few minutes only to check whether the hourly run is due;
///     cadence and backoff are enforced from persisted state inside <see cref="TorBoxOrphanCleanup" />.
/// </summary>
public class TorBoxOrphanCleanupService(ILogger<TorBoxOrphanCleanupService> logger, IServiceProvider serviceProvider) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!Startup.Ready)
        {
            await Task.Delay(1000, stoppingToken);
        }

        // Let ProviderUpdater sync local state after a restart before comparing against TorBox.
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<TorBoxOrphanCleanup>().RunOnce();

                if (result.Outcome == OrphanCleanupOutcome.Evaluated)
                {
                    logger.LogDebug("TorBox cleanup run complete: {Reported} reported, {Removed} removed", result.Reported.Count, result.Removed.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogError("TorBox cleanup run failed unexpectedly, no further action this cycle: {Message}", ex.Message);
            }

            await Task.Delay(WakeInterval, stoppingToken);
        }
    }
}
