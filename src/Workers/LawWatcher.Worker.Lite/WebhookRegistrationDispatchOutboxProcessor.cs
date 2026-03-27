using System.Text.Json;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Contracts;

namespace LawWatcher.Worker.Lite;

public sealed record WebhookRegistrationDispatchOutboxProcessingResult(
    int ProcessedCount,
    bool HasRemainingMessages);

public sealed class WebhookRegistrationDispatchOutboxProcessor(
    AlertWebhookDispatchService alertWebhookDispatchService,
    IOutboxMessageStore? outboxMessageStore = null,
    IInboxStore? inboxStore = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] MessageTypes =
    [
        typeof(WebhookRegisteredIntegrationEvent).FullName ?? nameof(WebhookRegisteredIntegrationEvent),
        typeof(WebhookUpdatedIntegrationEvent).FullName ?? nameof(WebhookUpdatedIntegrationEvent),
        typeof(WebhookDeactivatedIntegrationEvent).FullName ?? nameof(WebhookDeactivatedIntegrationEvent)
    ];

    public const string ConsumerName = "worker-lite.webhook-registration-dispatch";

    public async Task<WebhookRegistrationDispatchOutboxProcessingResult> ProcessAvailableAsync(int maxMessages, CancellationToken cancellationToken)
    {
        if (maxMessages <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessages), maxMessages, "Maximum batch size must be greater than zero.");
        }

        if (outboxMessageStore?.SupportsPolling != true || inboxStore is null)
        {
            return new WebhookRegistrationDispatchOutboxProcessingResult(0, false);
        }

        var messages = await outboxMessageStore.GetPendingAsync(MessageTypes, maxMessages, cancellationToken);
        var processedCount = 0;
        var unprocessedMessages = new List<OutboxMessage>();

        foreach (var message in messages)
        {
            if (await inboxStore.HasProcessedAsync(message.MessageId, ConsumerName, cancellationToken))
            {
                await outboxMessageStore.MarkPublishedAsync(message.MessageId, DateTimeOffset.UtcNow, cancellationToken);
                processedCount++;
                continue;
            }

            try
            {
                ValidateMessage(message);
                unprocessedMessages.Add(message);
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

        if (unprocessedMessages.Count != 0)
        {
            await alertWebhookDispatchService.DispatchPendingAsync(cancellationToken);

            foreach (var message in unprocessedMessages)
            {
                await inboxStore.MarkProcessedAsync(message.MessageId, ConsumerName, cancellationToken);
                await outboxMessageStore.MarkPublishedAsync(message.MessageId, DateTimeOffset.UtcNow, cancellationToken);
                processedCount++;
            }
        }

        var hasRemainingMessages = (await outboxMessageStore.GetPendingAsync(MessageTypes, 1, cancellationToken)).Count != 0;
        return new WebhookRegistrationDispatchOutboxProcessingResult(processedCount, hasRemainingMessages);
    }

    private static void ValidateMessage(OutboxMessage message)
    {
        if (string.Equals(message.MessageType, typeof(WebhookRegisteredIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            _ = JsonSerializer.Deserialize<WebhookRegisteredIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(WebhookRegisteredIntegrationEvent)}'.");
            return;
        }

        if (string.Equals(message.MessageType, typeof(WebhookUpdatedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            _ = JsonSerializer.Deserialize<WebhookUpdatedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(WebhookUpdatedIntegrationEvent)}'.");
            return;
        }

        if (string.Equals(message.MessageType, typeof(WebhookDeactivatedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            _ = JsonSerializer.Deserialize<WebhookDeactivatedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(WebhookDeactivatedIntegrationEvent)}'.");
            return;
        }

        throw new InvalidOperationException($"Unsupported outbox message type '{message.MessageType}' for '{nameof(WebhookRegistrationDispatchOutboxProcessor)}'.");
    }
}
