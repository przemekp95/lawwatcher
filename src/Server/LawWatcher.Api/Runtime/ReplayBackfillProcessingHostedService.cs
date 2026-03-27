using LawWatcher.IntegrationApi.Application;

namespace LawWatcher.Api.Runtime;

public sealed class ReplayBackfillProcessingHostedService(
    ISystemCapabilitiesProvider capabilitiesProvider,
    ReplayQueueProcessor replayQueueProcessor,
    BackfillQueueProcessor backfillQueueProcessor,
    ILogger<ReplayBackfillProcessingHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!capabilitiesProvider.Current.ReplayEnabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                continue;
            }

            var replayBatch = await replayQueueProcessor.ProcessAvailableAsync(1, stoppingToken);
            var backfillBatch = await backfillQueueProcessor.ProcessAvailableAsync(1, stoppingToken);

            if (replayBatch.ProcessedCount > 0 || backfillBatch.ProcessedCount > 0)
            {
                logger.LogInformation(
                    "Processed replay/backfill queue. Replays={ReplayCount} Backfills={BackfillCount} RemainingReplays={RemainingReplays} RemainingBackfills={RemainingBackfills}",
                    replayBatch.ProcessedCount,
                    backfillBatch.ProcessedCount,
                    replayBatch.HasRemainingQueuedRequests,
                    backfillBatch.HasRemainingQueuedRequests);

                if (replayBatch.HasRemainingQueuedRequests || backfillBatch.HasRemainingQueuedRequests)
                {
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
