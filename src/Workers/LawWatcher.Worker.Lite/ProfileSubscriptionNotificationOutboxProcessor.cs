using System.Text.Json;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.Notifications.Application;
using LawWatcher.TaxonomyAndProfiles.Contracts;

namespace LawWatcher.Worker.Lite;

public sealed record ProfileSubscriptionNotificationOutboxProcessingResult(
    int ProcessedCount,
    bool HasRemainingMessages);

public sealed class ProfileSubscriptionNotificationOutboxProcessor(
    AlertNotificationDispatchService alertNotificationDispatchService,
    IOutboxMessageStore? outboxMessageStore = null,
    IInboxStore? inboxStore = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] MessageTypes =
    [
        typeof(ProfileSubscriptionCreatedIntegrationEvent).FullName ?? nameof(ProfileSubscriptionCreatedIntegrationEvent),
        typeof(ProfileSubscriptionAlertPolicyChangedIntegrationEvent).FullName ?? nameof(ProfileSubscriptionAlertPolicyChangedIntegrationEvent),
        typeof(ProfileSubscriptionDeactivatedIntegrationEvent).FullName ?? nameof(ProfileSubscriptionDeactivatedIntegrationEvent)
    ];

    public const string ConsumerName = "worker-lite.profile-subscription-notification-dispatch";

    public async Task<ProfileSubscriptionNotificationOutboxProcessingResult> ProcessAvailableAsync(int maxMessages, CancellationToken cancellationToken)
    {
        if (maxMessages <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessages), maxMessages, "Maximum batch size must be greater than zero.");
        }

        if (outboxMessageStore?.SupportsPolling != true || inboxStore is null)
        {
            return new ProfileSubscriptionNotificationOutboxProcessingResult(0, false);
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
            await alertNotificationDispatchService.DispatchPendingAsync(cancellationToken);

            foreach (var message in unprocessedMessages)
            {
                await inboxStore.MarkProcessedAsync(message.MessageId, ConsumerName, cancellationToken);
                await outboxMessageStore.MarkPublishedAsync(message.MessageId, DateTimeOffset.UtcNow, cancellationToken);
                processedCount++;
            }
        }

        var hasRemainingMessages = (await outboxMessageStore.GetPendingAsync(MessageTypes, 1, cancellationToken)).Count != 0;
        return new ProfileSubscriptionNotificationOutboxProcessingResult(processedCount, hasRemainingMessages);
    }

    private static void ValidateMessage(OutboxMessage message)
    {
        if (string.Equals(message.MessageType, typeof(ProfileSubscriptionCreatedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            _ = JsonSerializer.Deserialize<ProfileSubscriptionCreatedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(ProfileSubscriptionCreatedIntegrationEvent)}'.");
            return;
        }

        if (string.Equals(message.MessageType, typeof(ProfileSubscriptionAlertPolicyChangedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            _ = JsonSerializer.Deserialize<ProfileSubscriptionAlertPolicyChangedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(ProfileSubscriptionAlertPolicyChangedIntegrationEvent)}'.");
            return;
        }

        if (string.Equals(message.MessageType, typeof(ProfileSubscriptionDeactivatedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            _ = JsonSerializer.Deserialize<ProfileSubscriptionDeactivatedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(ProfileSubscriptionDeactivatedIntegrationEvent)}'.");
            return;
        }

        throw new InvalidOperationException($"Unsupported outbox message type '{message.MessageType}' for '{nameof(ProfileSubscriptionNotificationOutboxProcessor)}'.");
    }
}
