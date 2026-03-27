using LawWatcher.AiEnrichment.Application;
using LawWatcher.BuildingBlocks.Configuration;
using Microsoft.Extensions.Options;

namespace LawWatcher.Worker.Ai;

public sealed class Worker(
    ILogger<Worker> logger,
    IConfiguration configuration,
    IOptionsMonitor<LawWatcherRuntimeOptions> runtimeOptions,
    IOptionsMonitor<LocalLlmWorkerOptions> llmOptions,
    AiEnrichmentQueueProcessor queueProcessor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rabbitMqEnabled = !string.IsNullOrWhiteSpace(configuration.GetConnectionString("RabbitMq"));
        var brokerModeLogged = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var runtimeProfile = RuntimeProfile.Parse(runtimeOptions.CurrentValue.Profile);
            var capabilities = SystemCapabilities.FromOptions(runtimeProfile, runtimeOptions.CurrentValue.Capabilities);
            var executionPolicy = LocalLlmExecutionPolicy.For(runtimeProfile);
            var configuredModel = llmOptions.CurrentValue.DefaultModel;

            if (!capabilities.Ai.Enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                continue;
            }

            if (rabbitMqEnabled)
            {
                if (!brokerModeLogged)
                {
                    logger.LogInformation(
                        "worker-ai broker consumer mode enabled. profile={Profile} configuredModel={Model}",
                        runtimeProfile.Value,
                        configuredModel);
                    brokerModeLogged = true;
                }

                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                continue;
            }

            var batchSize = Math.Max(1, Math.Min(executionPolicy.MaxConcurrency, llmOptions.CurrentValue.MaxConcurrency));
            var batchResult = await queueProcessor.ProcessAvailableAsync(batchSize, stoppingToken);

            if (batchResult.ProcessedCount > 0)
            {
                logger.LogInformation(
                    "worker-ai processed queued tasks. profile={Profile} activation={ActivationMode} configuredModel={Model} processed={ProcessedCount} remainingQueued={HasRemainingQueuedTasks} unloadAfterIdleSeconds={UnloadAfterIdleSeconds}",
                    runtimeProfile.Value,
                    executionPolicy.ActivationMode,
                    configuredModel,
                    batchResult.ProcessedCount,
                    batchResult.HasRemainingQueuedTasks,
                    llmOptions.CurrentValue.UnloadAfterIdleSeconds);

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
