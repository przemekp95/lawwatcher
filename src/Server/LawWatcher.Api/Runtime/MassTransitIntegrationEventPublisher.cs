using LawWatcher.BuildingBlocks.Messaging;
using MassTransit;

namespace LawWatcher.Api.Runtime;

public sealed class MassTransitIntegrationEventPublisher(IBus bus) : IIntegrationEventPublisher
{
    public Task PublishAsync<TIntegrationEvent>(TIntegrationEvent integrationEvent, CancellationToken cancellationToken)
        where TIntegrationEvent : class, IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return bus.Publish(integrationEvent, cancellationToken);
    }
}
