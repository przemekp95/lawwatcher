using LawWatcher.IntegrationApi.Application;

namespace LawWatcher.Api.Runtime;

public sealed class ReplayBackfillBrokerPublishingHostedService(
    ILogger<ReplayBackfillBrokerPublishingHostedService> logger,
    ReplayRequestedOutboxPublisher replayOutboxPublisher,
    BackfillRequestedOutboxPublisher backfillOutboxPublisher) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var replayBatch = await replayOutboxPublisher.PublishPendingAsync(maxMessages: 16, stoppingToken);
            var backfillBatch = await backfillOutboxPublisher.PublishPendingAsync(maxMessages: 16, stoppingToken);

            if (replayBatch.PublishedCount > 0)
            {
                logger.LogInformation(
                    "api broker publish batch completed. flow=replay published={PublishedCount} hasRemainingMessages={HasRemainingMessages}",
                    replayBatch.PublishedCount,
                    replayBatch.HasRemainingMessages);
            }

            if (backfillBatch.PublishedCount > 0)
            {
                logger.LogInformation(
                    "api broker publish batch completed. flow=backfill published={PublishedCount} hasRemainingMessages={HasRemainingMessages}",
                    backfillBatch.PublishedCount,
                    backfillBatch.HasRemainingMessages);
            }

            if (replayBatch.PublishedCount > 0 || backfillBatch.PublishedCount > 0)
            {
                if (replayBatch.HasRemainingMessages || backfillBatch.HasRemainingMessages)
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
