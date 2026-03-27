using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Contracts;
using MassTransit;

namespace LawWatcher.Worker.Lite;

public sealed class ReplayRequestedConsumer(
    ILogger<ReplayRequestedConsumer> logger,
    ReplayRequestedMessageHandler messageHandler) : IConsumer<ReplayRequestedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<ReplayRequestedIntegrationEvent> context)
    {
        var result = await messageHandler.HandleAsync(context.Message, context.CancellationToken);
        logger.LogInformation(
            "worker-lite broker message handled. flow=replay requestId={RequestId} eventId={EventId} hasProcessedRequest={HasProcessedRequest} status={Status}",
            context.Message.ReplayRequestId,
            context.Message.EventId,
            result.HasProcessedRequest,
            result.Status);
    }
}

public sealed class BackfillRequestedConsumer(
    ILogger<BackfillRequestedConsumer> logger,
    BackfillRequestedMessageHandler messageHandler) : IConsumer<BackfillRequestedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<BackfillRequestedIntegrationEvent> context)
    {
        var result = await messageHandler.HandleAsync(context.Message, context.CancellationToken);
        logger.LogInformation(
            "worker-lite broker message handled. flow=backfill requestId={RequestId} eventId={EventId} hasProcessedRequest={HasProcessedRequest} status={Status}",
            context.Message.BackfillRequestId,
            context.Message.EventId,
            result.HasProcessedRequest,
            result.Status);
    }
}
