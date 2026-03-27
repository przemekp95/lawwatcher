using LawWatcher.AiEnrichment.Application;

namespace LawWatcher.Api.Runtime;

public sealed class AiEnrichmentBrokerPublishingHostedService(
    ILogger<AiEnrichmentBrokerPublishingHostedService> logger,
    AiEnrichmentRequestedOutboxPublisher outboxPublisher) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var batch = await outboxPublisher.PublishPendingAsync(maxMessages: 16, stoppingToken);
            if (batch.PublishedCount > 0)
            {
                logger.LogInformation(
                    "api broker publish batch completed. flow=ai published={PublishedCount} hasRemainingMessages={HasRemainingMessages}",
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
