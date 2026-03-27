using LawWatcher.SearchAndDiscovery.Application;
using LawWatcher.TaxonomyAndProfiles.Application;
using LawWatcher.TaxonomyAndProfiles.Contracts;
using MassTransit;

namespace LawWatcher.Worker.Lite;

public sealed class MonitoringProfileCreatedConsumer(
    MonitoringProfileProjectionMessageHandler messageHandler) : IConsumer<MonitoringProfileCreatedIntegrationEvent>
{
    public Task Consume(ConsumeContext<MonitoringProfileCreatedIntegrationEvent> context)
    {
        return messageHandler.HandleAsync(context.Message, context.CancellationToken);
    }
}

public sealed class MonitoringProfileRuleAddedConsumer(
    MonitoringProfileProjectionMessageHandler messageHandler) : IConsumer<MonitoringProfileRuleAddedIntegrationEvent>
{
    public Task Consume(ConsumeContext<MonitoringProfileRuleAddedIntegrationEvent> context)
    {
        return messageHandler.HandleAsync(context.Message, context.CancellationToken);
    }
}

public sealed class MonitoringProfileAlertPolicyChangedConsumer(
    MonitoringProfileProjectionMessageHandler messageHandler) : IConsumer<MonitoringProfileAlertPolicyChangedIntegrationEvent>
{
    public Task Consume(ConsumeContext<MonitoringProfileAlertPolicyChangedIntegrationEvent> context)
    {
        return messageHandler.HandleAsync(context.Message, context.CancellationToken);
    }
}

public sealed class MonitoringProfileDeactivatedConsumer(
    MonitoringProfileProjectionMessageHandler messageHandler) : IConsumer<MonitoringProfileDeactivatedIntegrationEvent>
{
    public Task Consume(ConsumeContext<MonitoringProfileDeactivatedIntegrationEvent> context)
    {
        return messageHandler.HandleAsync(context.Message, context.CancellationToken);
    }
}

public sealed class MonitoringProfileProjectionRefreshOrchestrator(
    AlertProjectionRefreshService alertProjectionRefreshService,
    SearchProjectionRefreshService searchProjectionRefreshService) : IMonitoringProfileProjectionRefreshOrchestrator
{
    public async Task<MonitoringProfileProjectionRefreshExecutionResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await alertProjectionRefreshService.RefreshAsync(cancellationToken);
        await searchProjectionRefreshService.RefreshAsync(cancellationToken);
        return new MonitoringProfileProjectionRefreshExecutionResult(true);
    }
}
