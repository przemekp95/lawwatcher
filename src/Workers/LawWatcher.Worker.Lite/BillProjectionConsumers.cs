using LawWatcher.LegislativeIntake.Application;
using LawWatcher.LegislativeIntake.Contracts;
using LawWatcher.IntegrationApi.Application;
using MassTransit;

namespace LawWatcher.Worker.Lite;

public sealed class BillImportedConsumer(
    BillProjectionMessageHandler messageHandler) : IConsumer<BillImportedIntegrationEvent>
{
    public Task Consume(ConsumeContext<BillImportedIntegrationEvent> context)
    {
        return messageHandler.HandleAsync(context.Message, context.CancellationToken);
    }
}

public sealed class BillDocumentAttachedConsumer(
    BillProjectionMessageHandler messageHandler) : IConsumer<BillDocumentAttachedIntegrationEvent>
{
    public Task Consume(ConsumeContext<BillDocumentAttachedIntegrationEvent> context)
    {
        return messageHandler.HandleAsync(context.Message, context.CancellationToken);
    }
}

public sealed class BillProjectionRefreshOrchestrator(
    AlertProjectionRefreshService alertProjectionRefreshService,
    EventFeedProjectionRefreshService eventFeedProjectionRefreshService,
    SearchProjectionRefreshService searchProjectionRefreshService) : IBillProjectionRefreshOrchestrator
{
    public async Task<BillProjectionRefreshExecutionResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await alertProjectionRefreshService.RefreshAsync(cancellationToken);
        await searchProjectionRefreshService.RefreshAsync(cancellationToken);
        await eventFeedProjectionRefreshService.RefreshAsync(cancellationToken);
        return new BillProjectionRefreshExecutionResult(true);
    }
}
