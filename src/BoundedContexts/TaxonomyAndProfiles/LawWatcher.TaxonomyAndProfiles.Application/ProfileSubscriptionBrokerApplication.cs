using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.TaxonomyAndProfiles.Contracts;
using System.Text.Json;

namespace LawWatcher.TaxonomyAndProfiles.Application;

public sealed record ProfileSubscriptionIntegrationEventPublishingBatchResult(
    int PublishedCount,
    bool HasRemainingMessages);

public sealed class ProfileSubscriptionOutboxPublisher(
    IOutboxMessageStore outboxMessageStore,
    IIntegrationEventPublisher integrationEventPublisher)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] MessageTypes =
    [
        typeof(ProfileSubscriptionCreatedIntegrationEvent).FullName ?? nameof(ProfileSubscriptionCreatedIntegrationEvent),
        typeof(ProfileSubscriptionAlertPolicyChangedIntegrationEvent).FullName ?? nameof(ProfileSubscriptionAlertPolicyChangedIntegrationEvent),
        typeof(ProfileSubscriptionDeactivatedIntegrationEvent).FullName ?? nameof(ProfileSubscriptionDeactivatedIntegrationEvent)
    ];

    public async Task<ProfileSubscriptionIntegrationEventPublishingBatchResult> PublishPendingAsync(int maxMessages, CancellationToken cancellationToken)
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
        return new ProfileSubscriptionIntegrationEventPublishingBatchResult(publishedCount, hasRemainingMessages);
    }

    private Task PublishMessageAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (string.Equals(message.MessageType, typeof(ProfileSubscriptionCreatedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            var integrationEvent = JsonSerializer.Deserialize<ProfileSubscriptionCreatedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(ProfileSubscriptionCreatedIntegrationEvent)}'.");
            return integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
        }

        if (string.Equals(message.MessageType, typeof(ProfileSubscriptionAlertPolicyChangedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            var integrationEvent = JsonSerializer.Deserialize<ProfileSubscriptionAlertPolicyChangedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(ProfileSubscriptionAlertPolicyChangedIntegrationEvent)}'.");
            return integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
        }

        if (string.Equals(message.MessageType, typeof(ProfileSubscriptionDeactivatedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            var integrationEvent = JsonSerializer.Deserialize<ProfileSubscriptionDeactivatedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(ProfileSubscriptionDeactivatedIntegrationEvent)}'.");
            return integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
        }

        throw new InvalidOperationException($"Unsupported outbox message type '{message.MessageType}' for '{nameof(ProfileSubscriptionOutboxPublisher)}'.");
    }
}
