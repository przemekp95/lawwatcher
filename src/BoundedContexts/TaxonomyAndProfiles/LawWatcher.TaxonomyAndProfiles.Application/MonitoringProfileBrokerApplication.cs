using System.Text.Json;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.TaxonomyAndProfiles.Contracts;

namespace LawWatcher.TaxonomyAndProfiles.Application;

public sealed record MonitoringProfileProjectionIntegrationEventPublishingBatchResult(
    int PublishedCount,
    bool HasRemainingMessages);

public sealed record MonitoringProfileProjectionRefreshExecutionResult(bool HasRefreshed);

public interface IMonitoringProfileProjectionRefreshOrchestrator
{
    Task<MonitoringProfileProjectionRefreshExecutionResult> RefreshAsync(CancellationToken cancellationToken);
}

public sealed class MonitoringProfileProjectionOutboxPublisher(
    IOutboxMessageStore outboxMessageStore,
    IIntegrationEventPublisher integrationEventPublisher)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] MessageTypes =
    [
        typeof(MonitoringProfileCreatedIntegrationEvent).FullName ?? nameof(MonitoringProfileCreatedIntegrationEvent),
        typeof(MonitoringProfileRuleAddedIntegrationEvent).FullName ?? nameof(MonitoringProfileRuleAddedIntegrationEvent),
        typeof(MonitoringProfileAlertPolicyChangedIntegrationEvent).FullName ?? nameof(MonitoringProfileAlertPolicyChangedIntegrationEvent),
        typeof(MonitoringProfileDeactivatedIntegrationEvent).FullName ?? nameof(MonitoringProfileDeactivatedIntegrationEvent)
    ];

    public async Task<MonitoringProfileProjectionIntegrationEventPublishingBatchResult> PublishPendingAsync(int maxMessages, CancellationToken cancellationToken)
    {
        if (maxMessages <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessages), maxMessages, "Maximum batch size must be greater than zero.");
        }

        var messages = await outboxMessageStore.GetPendingAsync(MessageTypes, maxMessages, cancellationToken);
        var publishedCount = 0;

        foreach (var message in messages)
        {
            try
            {
                await PublishMessageAsync(message, cancellationToken);
                await outboxMessageStore.MarkPublishedAsync(message.MessageId, DateTimeOffset.UtcNow, cancellationToken);
                publishedCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await outboxMessageStore.DeferAsync(message.MessageId, DateTimeOffset.UtcNow.AddSeconds(15), cancellationToken);
            }
        }

        var hasRemainingMessages = (await outboxMessageStore.GetPendingAsync(MessageTypes, 1, cancellationToken)).Count != 0;
        return new MonitoringProfileProjectionIntegrationEventPublishingBatchResult(publishedCount, hasRemainingMessages);
    }

    private Task PublishMessageAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (string.Equals(message.MessageType, typeof(MonitoringProfileCreatedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            var integrationEvent = JsonSerializer.Deserialize<MonitoringProfileCreatedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(MonitoringProfileCreatedIntegrationEvent)}'.");
            return integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
        }

        if (string.Equals(message.MessageType, typeof(MonitoringProfileRuleAddedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            var integrationEvent = JsonSerializer.Deserialize<MonitoringProfileRuleAddedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(MonitoringProfileRuleAddedIntegrationEvent)}'.");
            return integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
        }

        if (string.Equals(message.MessageType, typeof(MonitoringProfileAlertPolicyChangedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            var integrationEvent = JsonSerializer.Deserialize<MonitoringProfileAlertPolicyChangedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(MonitoringProfileAlertPolicyChangedIntegrationEvent)}'.");
            return integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
        }

        if (string.Equals(message.MessageType, typeof(MonitoringProfileDeactivatedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            var integrationEvent = JsonSerializer.Deserialize<MonitoringProfileDeactivatedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(MonitoringProfileDeactivatedIntegrationEvent)}'.");
            return integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
        }

        throw new InvalidOperationException($"Unsupported outbox message type '{message.MessageType}' for '{nameof(MonitoringProfileProjectionOutboxPublisher)}'.");
    }
}

public sealed class MonitoringProfileProjectionMessageHandler(
    IMonitoringProfileProjectionRefreshOrchestrator projectionRefreshOrchestrator,
    IInboxStore inboxStore)
{
    public const string ConsumerName = "worker-lite.monitoring-profile-projection-refresh";

    public Task<MonitoringProfileProjectionRefreshExecutionResult> HandleAsync(
        MonitoringProfileCreatedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return HandleCoreAsync(integrationEvent.EventId, cancellationToken);
    }

    public Task<MonitoringProfileProjectionRefreshExecutionResult> HandleAsync(
        MonitoringProfileRuleAddedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return HandleCoreAsync(integrationEvent.EventId, cancellationToken);
    }

    public Task<MonitoringProfileProjectionRefreshExecutionResult> HandleAsync(
        MonitoringProfileAlertPolicyChangedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return HandleCoreAsync(integrationEvent.EventId, cancellationToken);
    }

    public Task<MonitoringProfileProjectionRefreshExecutionResult> HandleAsync(
        MonitoringProfileDeactivatedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return HandleCoreAsync(integrationEvent.EventId, cancellationToken);
    }

    private async Task<MonitoringProfileProjectionRefreshExecutionResult> HandleCoreAsync(Guid eventId, CancellationToken cancellationToken)
    {
        if (await inboxStore.HasProcessedAsync(eventId, ConsumerName, cancellationToken))
        {
            return new MonitoringProfileProjectionRefreshExecutionResult(false);
        }

        var result = await projectionRefreshOrchestrator.RefreshAsync(cancellationToken);
        await inboxStore.MarkProcessedAsync(eventId, ConsumerName, cancellationToken);
        return result;
    }
}
