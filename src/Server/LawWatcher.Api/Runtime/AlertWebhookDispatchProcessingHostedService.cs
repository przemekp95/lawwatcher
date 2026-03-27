using LawWatcher.IntegrationApi.Application;

namespace LawWatcher.Api.Runtime;

public sealed class AlertWebhookDispatchProcessingHostedService(
    AlertWebhookDispatchService dispatchService,
    ILogger<AlertWebhookDispatchProcessingHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await dispatchService.DispatchPendingAsync(stoppingToken);
            if (result.ProcessedCount > 0)
            {
                logger.LogInformation(
                    "Dispatched {ProcessedCount} integration webhook event(s) for alerts.",
                    result.ProcessedCount);

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
