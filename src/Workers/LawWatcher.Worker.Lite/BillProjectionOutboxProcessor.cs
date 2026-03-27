using System.Text.Json;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.LegislativeIntake.Application;
using LawWatcher.LegislativeIntake.Contracts;

namespace LawWatcher.Worker.Lite;

public sealed record BillProjectionOutboxProcessingResult(
    int ProcessedCount,
    bool HasRemainingMessages);

public sealed class BillProjectionOutboxProcessor(
    AlertProjectionRefreshService alertProjectionRefreshService,
    EventFeedProjectionRefreshService eventFeedProjectionRefreshService,
    SearchProjectionRefreshService searchProjectionRefreshService,
    IOutboxMessageStore? outboxMessageStore = null,
    IInboxStore? inboxStore = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] MessageTypes =
    [
        typeof(BillImportedIntegrationEvent).FullName ?? nameof(BillImportedIntegrationEvent),
        typeof(BillDocumentAttachedIntegrationEvent).FullName ?? nameof(BillDocumentAttachedIntegrationEvent)
    ];

    public const string ConsumerName = BillProjectionMessageHandler.ConsumerName;

    public async Task<BillProjectionOutboxProcessingResult> ProcessAvailableAsync(int maxMessages, CancellationToken cancellationToken)
    {
        if (maxMessages <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessages), maxMessages, "Maximum batch size must be greater than zero.");
        }

        if (outboxMessageStore?.SupportsPolling != true || inboxStore is null)
        {
            return new BillProjectionOutboxProcessingResult(0, false);
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
            await eventFeedProjectionRefreshService.RefreshAsync(cancellationToken);

            foreach (var message in unprocessedMessages)
            {
                await inboxStore.MarkProcessedAsync(message.MessageId, ConsumerName, cancellationToken);
                await outboxMessageStore.MarkPublishedAsync(message.MessageId, DateTimeOffset.UtcNow, cancellationToken);
                processedCount++;
            }
        }

        var hasRemainingMessages = (await outboxMessageStore.GetPendingAsync(MessageTypes, 1, cancellationToken)).Count != 0;
        return new BillProjectionOutboxProcessingResult(processedCount, hasRemainingMessages);
    }

    private static void ValidateMessage(OutboxMessage message)
    {
        if (string.Equals(message.MessageType, typeof(BillImportedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            _ = JsonSerializer.Deserialize<BillImportedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(BillImportedIntegrationEvent)}'.");
            return;
        }

        if (string.Equals(message.MessageType, typeof(BillDocumentAttachedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            _ = JsonSerializer.Deserialize<BillDocumentAttachedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(BillDocumentAttachedIntegrationEvent)}'.");
            return;
        }

        throw new InvalidOperationException($"Unsupported outbox message type '{message.MessageType}' for '{nameof(BillProjectionOutboxProcessor)}'.");
    }
}
