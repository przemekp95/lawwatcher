using LawWatcher.Notifications.Application;

namespace LawWatcher.Api.Runtime;

public sealed class AlertNotificationProcessingHostedService(
    AlertNotificationDispatchService dispatchService,
    ILogger<AlertNotificationProcessingHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await dispatchService.DispatchPendingAsync(stoppingToken);
            if (result.ProcessedCount > 0)
            {
                logger.LogInformation(
                    "Dispatched {ProcessedCount} alert notification(s). SkippedDigest={SkippedDigestCount}",
                    result.ProcessedCount,
                    result.SkippedDigestCount);

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
