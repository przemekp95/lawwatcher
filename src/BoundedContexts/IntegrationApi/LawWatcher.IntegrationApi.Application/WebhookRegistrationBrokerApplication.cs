using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Contracts;
using System.Text.Json;

namespace LawWatcher.IntegrationApi.Application;

public sealed record WebhookRegistrationIntegrationEventPublishingBatchResult(
    int PublishedCount,
    bool HasRemainingMessages);

public sealed class WebhookRegistrationOutboxPublisher(
    IOutboxMessageStore outboxMessageStore,
    IIntegrationEventPublisher integrationEventPublisher)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] MessageTypes =
    [
        typeof(WebhookRegisteredIntegrationEvent).FullName ?? nameof(WebhookRegisteredIntegrationEvent),
        typeof(WebhookUpdatedIntegrationEvent).FullName ?? nameof(WebhookUpdatedIntegrationEvent),
        typeof(WebhookDeactivatedIntegrationEvent).FullName ?? nameof(WebhookDeactivatedIntegrationEvent)
    ];

    public async Task<WebhookRegistrationIntegrationEventPublishingBatchResult> PublishPendingAsync(int maxMessages, CancellationToken cancellationToken)
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
        return new WebhookRegistrationIntegrationEventPublishingBatchResult(publishedCount, hasRemainingMessages);
    }

    private Task PublishMessageAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (string.Equals(message.MessageType, typeof(WebhookRegisteredIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            var integrationEvent = JsonSerializer.Deserialize<WebhookRegisteredIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(WebhookRegisteredIntegrationEvent)}'.");
            return integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
        }

        if (string.Equals(message.MessageType, typeof(WebhookUpdatedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            var integrationEvent = JsonSerializer.Deserialize<WebhookUpdatedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(WebhookUpdatedIntegrationEvent)}'.");
            return integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
        }

        if (string.Equals(message.MessageType, typeof(WebhookDeactivatedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            var integrationEvent = JsonSerializer.Deserialize<WebhookDeactivatedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(WebhookDeactivatedIntegrationEvent)}'.");
            return integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
        }

        throw new InvalidOperationException($"Unsupported outbox message type '{message.MessageType}' for '{nameof(WebhookRegistrationOutboxPublisher)}'.");
    }
}

public sealed class WebhookRegistrationDispatchMessageHandler(
    AlertWebhookDispatchService alertWebhookDispatchService,
    IInboxStore inboxStore)
{
    public const string ConsumerName = "worker-lite.webhook-registration-dispatch";

    public Task<AlertWebhookDispatchResult> HandleAsync(
        WebhookRegisteredIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return HandleCoreAsync(integrationEvent.EventId, cancellationToken);
    }

    public Task<AlertWebhookDispatchResult> HandleAsync(
        WebhookUpdatedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return HandleCoreAsync(integrationEvent.EventId, cancellationToken);
    }

    public Task<AlertWebhookDispatchResult> HandleAsync(
        WebhookDeactivatedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return HandleCoreAsync(integrationEvent.EventId, cancellationToken);
    }

    private async Task<AlertWebhookDispatchResult> HandleCoreAsync(Guid eventId, CancellationToken cancellationToken)
    {
        if (await inboxStore.HasProcessedAsync(eventId, ConsumerName, cancellationToken))
        {
            return new AlertWebhookDispatchResult(0);
        }

        var result = await alertWebhookDispatchService.DispatchPendingAsync(cancellationToken);
        await inboxStore.MarkProcessedAsync(eventId, ConsumerName, cancellationToken);
        return result;
    }
}
