using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Contracts;

namespace LawWatcher.IntegrationApi.Application;

public sealed class MessagingDiagnosticsQueryService(
    IMessagingDiagnosticsStore store,
    bool sqlOutboxEnabled,
    bool brokerEnabled)
{
    public async Task<MessagingDiagnosticsResponse> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await store.GetSnapshotAsync(cancellationToken);
        return new MessagingDiagnosticsResponse(
            brokerEnabled ? "rabbitmq" : sqlOutboxEnabled ? "sql-poller" : "inline",
            brokerEnabled ? "fallback" : sqlOutboxEnabled ? "primary" : "disabled",
            brokerEnabled,
            sqlOutboxEnabled,
            snapshot.IsAvailable,
            new MessagingOutboxResponse(
                snapshot.Outbox.TotalCount,
                snapshot.Outbox.PendingCount,
                snapshot.Outbox.ReadyCount,
                snapshot.Outbox.DeferredCount,
                snapshot.Outbox.PublishedCount,
                snapshot.Outbox.MaxAttemptCount,
                snapshot.Outbox.OldestPendingCreatedAtUtc,
                snapshot.Outbox.NextScheduledAttemptAtUtc,
                snapshot.Outbox.MessageTypes
                    .Select(messageType => new MessagingOutboxMessageTypeResponse(
                        messageType.MessageType,
                        messageType.TotalCount,
                        messageType.PendingCount,
                        messageType.ReadyCount,
                        messageType.DeferredCount,
                        messageType.PublishedCount,
                        messageType.MaxAttemptCount))
                    .ToArray()),
            new MessagingInboxResponse(
                snapshot.Inbox.ProcessedCount,
                snapshot.Inbox.Consumers
                    .Select(consumer => new MessagingInboxConsumerResponse(
                        consumer.ConsumerName,
                        consumer.ProcessedCount,
                        consumer.LastProcessedAtUtc))
                    .ToArray()));
    }
}
