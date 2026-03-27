using LawWatcher.AiEnrichment.Contracts;
using LawWatcher.BuildingBlocks.Messaging;
using MassTransit;

namespace LawWatcher.Worker.Lite;

public sealed record DocumentTextProjectionRefreshExecutionResult(bool HasRefreshed);

public sealed class DocumentTextProjectionMessageHandler(
    SearchProjectionRefreshService searchProjectionRefreshService,
    IInboxStore inboxStore)
{
    public const string ConsumerName = "worker-lite.document-text-projection-refresh";

    public async Task<DocumentTextProjectionRefreshExecutionResult> HandleAsync(
        DocumentTextExtractedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        if (await inboxStore.HasProcessedAsync(integrationEvent.EventId, ConsumerName, cancellationToken))
        {
            return new DocumentTextProjectionRefreshExecutionResult(false);
        }

        var refreshResult = await searchProjectionRefreshService.RefreshAsync(cancellationToken);
        await inboxStore.MarkProcessedAsync(integrationEvent.EventId, ConsumerName, cancellationToken);
        return new DocumentTextProjectionRefreshExecutionResult(refreshResult.HasRebuilt);
    }
}

public sealed class DocumentTextExtractedConsumer(
    ILogger<DocumentTextExtractedConsumer> logger,
    DocumentTextProjectionMessageHandler messageHandler) : IConsumer<DocumentTextExtractedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<DocumentTextExtractedIntegrationEvent> context)
    {
        var result = await messageHandler.HandleAsync(context.Message, context.CancellationToken);
        logger.LogInformation(
            "worker-lite broker message handled. flow=document-text-projection ownerType={OwnerType} ownerId={OwnerId} sourceObjectKey={SourceObjectKey} derivedObjectKey={DerivedObjectKey} hasRefreshed={HasRefreshed}",
            context.Message.OwnerType,
            context.Message.OwnerId,
            context.Message.SourceObjectKey,
            context.Message.DerivedObjectKey,
            result.HasRefreshed);
    }
}
