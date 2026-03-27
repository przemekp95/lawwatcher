using LawWatcher.LegislativeIntake.Application;

namespace LawWatcher.Api.Runtime;

public sealed class BillProjectionBrokerPublishingHostedService(
    ILogger<BillProjectionBrokerPublishingHostedService> logger,
    BillProjectionOutboxPublisher outboxPublisher) : BackgroundService
{
    private static readonly TimeSpan InitialBrokerStartupDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give downstream RabbitMQ consumers time to declare their durable queues before
        // the API drains startup seed events from the SQL outbox.
        await Task.Delay(InitialBrokerStartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var batch = await outboxPublisher.PublishPendingAsync(maxMessages: 16, stoppingToken);
            if (batch.PublishedCount > 0)
            {
                logger.LogInformation(
                    "api published bill projection integration events to RabbitMQ. published={PublishedCount} remaining={HasRemainingMessages}",
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
