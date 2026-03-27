using LawWatcher.Notifications.Application;

namespace LawWatcher.Api.Runtime;

public sealed class BillAlertBrokerPublishingHostedService(
    ILogger<BillAlertBrokerPublishingHostedService> logger,
    BillAlertCreatedOutboxPublisher outboxPublisher) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var batch = await outboxPublisher.PublishPendingAsync(maxMessages: 16, stoppingToken);
            if (batch.PublishedCount > 0)
            {
                logger.LogInformation(
                    "api published bill alert integration events to RabbitMQ. published={PublishedCount} remaining={HasRemainingMessages}",
                    batch.PublishedCount,
                    batch.HasRemainingMessages);

                if (batch.HasRemainingMessages)
                {
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }
}
