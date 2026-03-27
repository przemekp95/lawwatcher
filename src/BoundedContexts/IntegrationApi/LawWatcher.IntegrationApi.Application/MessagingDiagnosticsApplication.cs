using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Contracts;

namespace LawWatcher.IntegrationApi.Application;

public sealed class MessagingDiagnosticsQueryService(
    IMessagingDiagnosticsStore store,
    IBrokerDiagnosticsStore brokerStore,
    bool sqlOutboxEnabled,
    bool brokerEnabled)
{
    public async Task<MessagingDiagnosticsResponse> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await store.GetSnapshotAsync(cancellationToken);
        var brokerSnapshot = await brokerStore.GetSnapshotAsync(cancellationToken);
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
                    .ToArray()),
            new MessagingBrokerResponse(
                brokerSnapshot.IsAvailable,
                brokerSnapshot.QueueCount,
                brokerSnapshot.ConsumerCount,
                brokerSnapshot.MessageCount,
                brokerSnapshot.ReadyCount,
                brokerSnapshot.UnackedCount,
                brokerSnapshot.FaultCount,
                brokerSnapshot.DeadLetterCount,
                brokerSnapshot.RedeliveryCount,
                brokerSnapshot.Endpoints
                    .Select(endpoint => new MessagingBrokerEndpointResponse(
                        endpoint.EndpointName,
                        endpoint.QueueName,
                        endpoint.Status,
                        endpoint.ConsumerCount,
                        endpoint.MessageCount,
                        endpoint.ReadyCount,
                        endpoint.UnackedCount,
                        endpoint.FaultCount,
                        endpoint.DeadLetterCount,
                        endpoint.RedeliveryCount))
                    .ToArray()));
    }
}
