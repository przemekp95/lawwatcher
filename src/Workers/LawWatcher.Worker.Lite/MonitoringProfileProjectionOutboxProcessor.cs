using System.Text.Json;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.SearchAndDiscovery.Application;
using LawWatcher.TaxonomyAndProfiles.Application;
using LawWatcher.TaxonomyAndProfiles.Contracts;

namespace LawWatcher.Worker.Lite;

public sealed record MonitoringProfileProjectionOutboxProcessingResult(
    int ProcessedCount,
    bool HasRemainingMessages);

public sealed class MonitoringProfileProjectionOutboxProcessor(
    AlertProjectionRefreshService alertProjectionRefreshService,
    SearchProjectionRefreshService searchProjectionRefreshService,
    IOutboxMessageStore? outboxMessageStore = null,
    IInboxStore? inboxStore = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] MessageTypes =
    [
        typeof(MonitoringProfileCreatedIntegrationEvent).FullName ?? nameof(MonitoringProfileCreatedIntegrationEvent),
        typeof(MonitoringProfileRuleAddedIntegrationEvent).FullName ?? nameof(MonitoringProfileRuleAddedIntegrationEvent),
        typeof(MonitoringProfileAlertPolicyChangedIntegrationEvent).FullName ?? nameof(MonitoringProfileAlertPolicyChangedIntegrationEvent),
        typeof(MonitoringProfileDeactivatedIntegrationEvent).FullName ?? nameof(MonitoringProfileDeactivatedIntegrationEvent)
    ];

    public const string ConsumerName = MonitoringProfileProjectionMessageHandler.ConsumerName;

    public async Task<MonitoringProfileProjectionOutboxProcessingResult> ProcessAvailableAsync(int maxMessages, CancellationToken cancellationToken)
    {
        if (maxMessages <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessages), maxMessages, "Maximum batch size must be greater than zero.");
        }

        if (outboxMessageStore?.SupportsPolling != true || inboxStore is null)
        {
            return new MonitoringProfileProjectionOutboxProcessingResult(0, false);
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
            await alertProjectionRefreshService.RefreshAsync(cancellationToken);
            await searchProjectionRefreshService.RefreshAsync(cancellationToken);

            foreach (var message in unprocessedMessages)
            {
                await inboxStore.MarkProcessedAsync(message.MessageId, ConsumerName, cancellationToken);
                await outboxMessageStore.MarkPublishedAsync(message.MessageId, DateTimeOffset.UtcNow, cancellationToken);
                processedCount++;
            }
        }

        var hasRemainingMessages = (await outboxMessageStore.GetPendingAsync(MessageTypes, 1, cancellationToken)).Count != 0;
        return new MonitoringProfileProjectionOutboxProcessingResult(processedCount, hasRemainingMessages);
    }

    private static void ValidateMessage(OutboxMessage message)
    {
        if (string.Equals(message.MessageType, typeof(MonitoringProfileCreatedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            _ = JsonSerializer.Deserialize<MonitoringProfileCreatedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(MonitoringProfileCreatedIntegrationEvent)}'.");
            return;
        }

        if (string.Equals(message.MessageType, typeof(MonitoringProfileRuleAddedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            _ = JsonSerializer.Deserialize<MonitoringProfileRuleAddedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(MonitoringProfileRuleAddedIntegrationEvent)}'.");
            return;
        }

        if (string.Equals(message.MessageType, typeof(MonitoringProfileAlertPolicyChangedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            _ = JsonSerializer.Deserialize<MonitoringProfileAlertPolicyChangedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(MonitoringProfileAlertPolicyChangedIntegrationEvent)}'.");
            return;
        }

        if (string.Equals(message.MessageType, typeof(MonitoringProfileDeactivatedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            _ = JsonSerializer.Deserialize<MonitoringProfileDeactivatedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(MonitoringProfileDeactivatedIntegrationEvent)}'.");
            return;
        }

        throw new InvalidOperationException($"Unsupported outbox message type '{message.MessageType}' for '{nameof(MonitoringProfileProjectionOutboxProcessor)}'.");
    }
}
