using System.Text.Json;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.LegislativeProcess.Contracts;

namespace LawWatcher.LegislativeProcess.Application;

public sealed record ProcessProjectionIntegrationEventPublishingBatchResult(
    int PublishedCount,
    bool HasRemainingMessages);

public sealed record ProcessProjectionRefreshExecutionResult(bool HasRefreshed);

public interface IProcessProjectionRefreshOrchestrator
{
    Task<ProcessProjectionRefreshExecutionResult> RefreshAsync(CancellationToken cancellationToken);
}

public sealed class ProcessProjectionOutboxPublisher(
    IOutboxMessageStore outboxMessageStore,
    IIntegrationEventPublisher integrationEventPublisher)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] MessageTypes =
    [
        typeof(LegislativeProcessStartedIntegrationEvent).FullName ?? nameof(LegislativeProcessStartedIntegrationEvent),
        typeof(LegislativeStageRecordedIntegrationEvent).FullName ?? nameof(LegislativeStageRecordedIntegrationEvent)
    ];

    public async Task<ProcessProjectionIntegrationEventPublishingBatchResult> PublishPendingAsync(int maxMessages, CancellationToken cancellationToken)
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
        return new ProcessProjectionIntegrationEventPublishingBatchResult(publishedCount, hasRemainingMessages);
    }

    private Task PublishMessageAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (string.Equals(message.MessageType, typeof(LegislativeProcessStartedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            var integrationEvent = JsonSerializer.Deserialize<LegislativeProcessStartedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(LegislativeProcessStartedIntegrationEvent)}'.");
            return integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
        }

        if (string.Equals(message.MessageType, typeof(LegislativeStageRecordedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            var integrationEvent = JsonSerializer.Deserialize<LegislativeStageRecordedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(LegislativeStageRecordedIntegrationEvent)}'.");
            return integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
        }

        throw new InvalidOperationException($"Unsupported outbox message type '{message.MessageType}' for '{nameof(ProcessProjectionOutboxPublisher)}'.");
    }
}

public sealed class ProcessProjectionMessageHandler(
    IProcessProjectionRefreshOrchestrator projectionRefreshOrchestrator,
    IInboxStore inboxStore)
{
    public const string ConsumerName = "worker-lite.process-projection-refresh";

    public Task<ProcessProjectionRefreshExecutionResult> HandleAsync(
        LegislativeProcessStartedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return HandleCoreAsync(integrationEvent.EventId, cancellationToken);
    }

    public Task<ProcessProjectionRefreshExecutionResult> HandleAsync(
        LegislativeStageRecordedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return HandleCoreAsync(integrationEvent.EventId, cancellationToken);
    }

    private async Task<ProcessProjectionRefreshExecutionResult> HandleCoreAsync(Guid eventId, CancellationToken cancellationToken)
    {
        if (await inboxStore.HasProcessedAsync(eventId, ConsumerName, cancellationToken))
        {
            return new ProcessProjectionRefreshExecutionResult(false);
        }

        var result = await projectionRefreshOrchestrator.RefreshAsync(cancellationToken);
        await inboxStore.MarkProcessedAsync(eventId, ConsumerName, cancellationToken);
        return result;
    }
}
