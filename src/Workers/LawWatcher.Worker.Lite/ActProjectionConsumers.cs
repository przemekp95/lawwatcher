using LawWatcher.IntegrationApi.Application;
using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegalCorpus.Contracts;
using MassTransit;

namespace LawWatcher.Worker.Lite;

public sealed class PublishedActRegisteredConsumer(
    ActProjectionMessageHandler messageHandler) : IConsumer<PublishedActRegisteredIntegrationEvent>
{
    public Task Consume(ConsumeContext<PublishedActRegisteredIntegrationEvent> context)
    {
        return messageHandler.HandleAsync(context.Message, context.CancellationToken);
    }
}

public sealed class ActArtifactAttachedConsumer(
    ActProjectionMessageHandler messageHandler) : IConsumer<ActArtifactAttachedIntegrationEvent>
{
    public Task Consume(ConsumeContext<ActArtifactAttachedIntegrationEvent> context)
    {
        return messageHandler.HandleAsync(context.Message, context.CancellationToken);
    }
}

public sealed class ActProjectionRefreshOrchestrator(
    EventFeedProjectionRefreshService eventFeedProjectionRefreshService,
    SearchProjectionRefreshService searchProjectionRefreshService) : IActProjectionRefreshOrchestrator
{
    public async Task<ActProjectionRefreshExecutionResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await searchProjectionRefreshService.RefreshAsync(cancellationToken);
        await eventFeedProjectionRefreshService.RefreshAsync(cancellationToken);
        return new ActProjectionRefreshExecutionResult(true);
    }
}
