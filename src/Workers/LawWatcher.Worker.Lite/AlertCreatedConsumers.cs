using LawWatcher.IntegrationApi.Application;
using LawWatcher.Notifications.Application;
using LawWatcher.Notifications.Contracts;
using MassTransit;

namespace LawWatcher.Worker.Lite;

public sealed class BillAlertNotificationDispatchConsumer(
    BillAlertNotificationMessageHandler messageHandler) : IConsumer<BillAlertCreatedIntegrationEvent>
{
    public Task Consume(ConsumeContext<BillAlertCreatedIntegrationEvent> context)
    {
        return messageHandler.HandleAsync(context.Message, context.CancellationToken);
    }
}

public sealed class BillAlertWebhookDispatchConsumer(
    BillAlertWebhookMessageHandler messageHandler) : IConsumer<BillAlertCreatedIntegrationEvent>
{
    public Task Consume(ConsumeContext<BillAlertCreatedIntegrationEvent> context)
    {
        return messageHandler.HandleAsync(context.Message, context.CancellationToken);
    }
}
