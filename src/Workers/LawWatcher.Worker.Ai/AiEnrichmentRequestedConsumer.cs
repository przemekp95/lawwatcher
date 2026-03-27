using LawWatcher.AiEnrichment.Application;
using LawWatcher.AiEnrichment.Contracts;
using MassTransit;

namespace LawWatcher.Worker.Ai;

public sealed class AiEnrichmentRequestedConsumer(
    ILogger<AiEnrichmentRequestedConsumer> logger,
    AiEnrichmentRequestedMessageHandler messageHandler) : IConsumer<AiEnrichmentRequestedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<AiEnrichmentRequestedIntegrationEvent> context)
    {
        var result = await messageHandler.HandleAsync(context.Message, context.CancellationToken);
        logger.LogInformation(
            "worker-ai broker message handled. flow=ai taskId={TaskId} eventId={EventId} hasProcessedTask={HasProcessedTask} status={Status}",
            context.Message.TaskId,
            context.Message.EventId,
            result.HasProcessedTask,
            result.Status);
    }
}
