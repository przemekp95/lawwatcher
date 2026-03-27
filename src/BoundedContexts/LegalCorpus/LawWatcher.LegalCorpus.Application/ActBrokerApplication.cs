using System.Text.Json;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.LegalCorpus.Contracts;

namespace LawWatcher.LegalCorpus.Application;

public sealed record ActProjectionIntegrationEventPublishingBatchResult(
    int PublishedCount,
    bool HasRemainingMessages);

public sealed record ActProjectionRefreshExecutionResult(bool HasRefreshed);

public interface IActProjectionRefreshOrchestrator
{
    Task<ActProjectionRefreshExecutionResult> RefreshAsync(CancellationToken cancellationToken);
}

public sealed class ActProjectionOutboxPublisher(
    IOutboxMessageStore outboxMessageStore,
    IIntegrationEventPublisher integrationEventPublisher)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] MessageTypes =
    [
        typeof(PublishedActRegisteredIntegrationEvent).FullName ?? nameof(PublishedActRegisteredIntegrationEvent),
        typeof(ActArtifactAttachedIntegrationEvent).FullName ?? nameof(ActArtifactAttachedIntegrationEvent)
    ];

    public async Task<ActProjectionIntegrationEventPublishingBatchResult> PublishPendingAsync(int maxMessages, CancellationToken cancellationToken)
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
        return new ActProjectionIntegrationEventPublishingBatchResult(publishedCount, hasRemainingMessages);
    }

    private Task PublishMessageAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (string.Equals(message.MessageType, typeof(PublishedActRegisteredIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            var integrationEvent = JsonSerializer.Deserialize<PublishedActRegisteredIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(PublishedActRegisteredIntegrationEvent)}'.");
            return integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
        }

        if (string.Equals(message.MessageType, typeof(ActArtifactAttachedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            var integrationEvent = JsonSerializer.Deserialize<ActArtifactAttachedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(ActArtifactAttachedIntegrationEvent)}'.");
            return integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
        }

        throw new InvalidOperationException($"Unsupported outbox message type '{message.MessageType}' for '{nameof(ActProjectionOutboxPublisher)}'.");
    }
}

public sealed class ActProjectionMessageHandler(
    IActProjectionRefreshOrchestrator projectionRefreshOrchestrator,
    IInboxStore inboxStore)
{
    public const string ConsumerName = "worker-lite.act-projection-refresh";

    public Task<ActProjectionRefreshExecutionResult> HandleAsync(
        PublishedActRegisteredIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return HandleCoreAsync(integrationEvent.EventId, cancellationToken);
    }

    public Task<ActProjectionRefreshExecutionResult> HandleAsync(
        ActArtifactAttachedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return HandleCoreAsync(integrationEvent.EventId, cancellationToken);
    }

    private async Task<ActProjectionRefreshExecutionResult> HandleCoreAsync(Guid eventId, CancellationToken cancellationToken)
    {
        if (await inboxStore.HasProcessedAsync(eventId, ConsumerName, cancellationToken))
        {
            return new ActProjectionRefreshExecutionResult(false);
        }

        var result = await projectionRefreshOrchestrator.RefreshAsync(cancellationToken);
        await inboxStore.MarkProcessedAsync(eventId, ConsumerName, cancellationToken);
        return result;
    }
}
