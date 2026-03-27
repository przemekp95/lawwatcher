using System.Text.Json;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.Notifications.Contracts;

namespace LawWatcher.Notifications.Application;

public sealed record BillAlertCreatedIntegrationEventPublishingBatchResult(
    int PublishedCount,
    bool HasRemainingMessages);

public sealed class BillAlertCreatedOutboxPublisher(
    IOutboxMessageStore outboxMessageStore,
    IIntegrationEventPublisher integrationEventPublisher)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] MessageTypes =
    [
        typeof(BillAlertCreatedIntegrationEvent).FullName ?? nameof(BillAlertCreatedIntegrationEvent)
    ];

    public async Task<BillAlertCreatedIntegrationEventPublishingBatchResult> PublishPendingAsync(int maxMessages, CancellationToken cancellationToken)
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
                var integrationEvent = JsonSerializer.Deserialize<BillAlertCreatedIntegrationEvent>(message.Payload, SerializerOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(BillAlertCreatedIntegrationEvent)}'.");
                await integrationEventPublisher.PublishAsync(integrationEvent, cancellationToken);
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
        return new BillAlertCreatedIntegrationEventPublishingBatchResult(publishedCount, hasRemainingMessages);
    }
}

public sealed class BillAlertNotificationMessageHandler(
    AlertNotificationDispatchService alertNotificationDispatchService,
    IInboxStore inboxStore)
{
    public const string ConsumerName = "worker-lite.bill-alert-notification-dispatch";

    public async Task<AlertNotificationDispatchResult> HandleAsync(
        BillAlertCreatedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        if (await inboxStore.HasProcessedAsync(integrationEvent.EventId, ConsumerName, cancellationToken))
        {
            return new AlertNotificationDispatchResult(0, 0);
        }

        var result = await alertNotificationDispatchService.DispatchAlertAsync(ToReadModel(integrationEvent), cancellationToken);
        await inboxStore.MarkProcessedAsync(integrationEvent.EventId, ConsumerName, cancellationToken);
        return result;
    }

    private static BillAlertReadModel ToReadModel(BillAlertCreatedIntegrationEvent integrationEvent)
    {
        return new BillAlertReadModel(
            integrationEvent.AlertId,
            integrationEvent.ProfileId,
            integrationEvent.ProfileName,
            integrationEvent.BillId,
            integrationEvent.BillTitle,
            integrationEvent.BillExternalId,
            integrationEvent.BillSubmittedOn,
            integrationEvent.AlertPolicy,
            integrationEvent.MatchedKeywords.ToArray(),
            integrationEvent.OccurredAtUtc);
    }
}
