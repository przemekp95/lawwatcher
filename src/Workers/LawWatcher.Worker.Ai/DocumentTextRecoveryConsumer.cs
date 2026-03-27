using LawWatcher.AiEnrichment.Application;
using LawWatcher.AiEnrichment.Contracts;
using MassTransit;

namespace LawWatcher.Worker.Ai;

public sealed class DocumentTextExtractedAiRecoveryConsumer(
    ILogger<DocumentTextExtractedAiRecoveryConsumer> logger,
    DocumentTextRecoveryMessageHandler messageHandler) : IConsumer<DocumentTextExtractedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<DocumentTextExtractedIntegrationEvent> context)
    {
        var result = await messageHandler.HandleAsync(context.Message, context.CancellationToken);
        logger.LogInformation(
            "worker-ai broker message handled. flow=ai-recovery ownerType={OwnerType} ownerId={OwnerId} sourceObjectKey={SourceObjectKey} derivedObjectKey={DerivedObjectKey} matchingQueuedTaskCount={MatchingQueuedTaskCount} processedTaskCount={ProcessedTaskCount} completedTaskCount={CompletedTaskCount} hasRemainingQueuedTasks={HasRemainingQueuedTasks}",
            context.Message.OwnerType,
            context.Message.OwnerId,
            context.Message.SourceObjectKey,
            context.Message.DerivedObjectKey,
            result.MatchingQueuedTaskCount,
            result.ProcessedTaskCount,
            result.CompletedTaskCount,
            result.HasRemainingQueuedTasks);
    }
}
