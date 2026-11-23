using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RdtClient.Service.Services.StalledJobs;

namespace RdtClient.Service.BackgroundServices;

/// <summary>
///     Hosts the TorBox stalled NZB watchdog. Wakes every few minutes only to check whether an hourly run is due;
///     the hourly cadence and backoff are enforced from persisted state inside <see cref="StalledJobWatchdog" />,
///     so no provider request is made more than once per hour and restarts do not trigger catch-up bursts.
/// </summary>
public class StalledJobWatchdogService(ILogger<StalledJobWatchdogService> logger, IServiceProvider serviceProvider) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!Startup.Ready)
        {
            await Task.Delay(1000, stoppingToken);
        }

        // Let ProviderUpdater refresh local provider state after a restart before the first evaluation.
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var watchdog = scope.ServiceProvider.GetRequiredService<StalledJobWatchdog>();

                var result = await watchdog.RunOnce();

                if (result.Outcome == StalledJobRunOutcome.Evaluated)
                {
                    logger.LogDebug("Stall watchdog run complete: {Stats}, {Failed} marked failed", result.Stats, result.Failed.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogError("Stall watchdog run failed unexpectedly, no further action this cycle: {Message}", ex.Message);
            }

            await Task.Delay(WakeInterval, stoppingToken);
        }
    }
}
