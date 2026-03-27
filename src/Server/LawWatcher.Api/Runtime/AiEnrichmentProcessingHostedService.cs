using LawWatcher.AiEnrichment.Application;

namespace LawWatcher.Api.Runtime;

public sealed class AiEnrichmentProcessingHostedService(
    ISystemCapabilitiesProvider capabilitiesProvider,
    AiEnrichmentQueueProcessor queueProcessor,
    ILogger<AiEnrichmentProcessingHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var capabilities = capabilitiesProvider.Current;
            if (!capabilities.Ai.Enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                continue;
            }

            var batchResult = await queueProcessor.ProcessAvailableAsync(
                Math.Max(1, capabilities.Ai.MaxConcurrency),
                stoppingToken);

            if (batchResult.ProcessedCount > 0)
            {
                logger.LogInformation(
                    "Processed {ProcessedCount} queued AI enrichment task(s). RemainingQueued={HasRemainingQueuedTasks}",
                    batchResult.ProcessedCount,
                    batchResult.HasRemainingQueuedTasks);

                if (batchResult.HasRemainingQueuedTasks)
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
