using LawWatcher.IntegrationApi.Application;
using LawWatcher.LegislativeProcess.Application;
using LawWatcher.LegislativeProcess.Contracts;
using MassTransit;

namespace LawWatcher.Worker.Lite;

public sealed class LegislativeProcessStartedConsumer(
    ProcessProjectionMessageHandler messageHandler) : IConsumer<LegislativeProcessStartedIntegrationEvent>
{
    public Task Consume(ConsumeContext<LegislativeProcessStartedIntegrationEvent> context)
    {
        return messageHandler.HandleAsync(context.Message, context.CancellationToken);
    }
}

public sealed class LegislativeStageRecordedConsumer(
    ProcessProjectionMessageHandler messageHandler) : IConsumer<LegislativeStageRecordedIntegrationEvent>
{
    public Task Consume(ConsumeContext<LegislativeStageRecordedIntegrationEvent> context)
    {
        return messageHandler.HandleAsync(context.Message, context.CancellationToken);
    }
}

public sealed class ProcessProjectionRefreshOrchestrator(
    EventFeedProjectionRefreshService eventFeedProjectionRefreshService,
    SearchProjectionRefreshService searchProjectionRefreshService) : IProcessProjectionRefreshOrchestrator
{
    public async Task<ProcessProjectionRefreshExecutionResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await searchProjectionRefreshService.RefreshAsync(cancellationToken);
        await eventFeedProjectionRefreshService.RefreshAsync(cancellationToken);
        return new ProcessProjectionRefreshExecutionResult(true);
    }
}
