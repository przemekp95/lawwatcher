using System.Text.Json;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.LegislativeProcess.Application;
using LawWatcher.LegislativeProcess.Contracts;

namespace LawWatcher.Worker.Lite;

public sealed record ProcessProjectionOutboxProcessingResult(
    int ProcessedCount,
    bool HasRemainingMessages);

public sealed class ProcessProjectionOutboxProcessor(
    EventFeedProjectionRefreshService eventFeedProjectionRefreshService,
    SearchProjectionRefreshService searchProjectionRefreshService,
    IOutboxMessageStore? outboxMessageStore = null,
    IInboxStore? inboxStore = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] MessageTypes =
    [
        typeof(LegislativeProcessStartedIntegrationEvent).FullName ?? nameof(LegislativeProcessStartedIntegrationEvent),
        typeof(LegislativeStageRecordedIntegrationEvent).FullName ?? nameof(LegislativeStageRecordedIntegrationEvent)
    ];

    public const string ConsumerName = ProcessProjectionMessageHandler.ConsumerName;

    public async Task<ProcessProjectionOutboxProcessingResult> ProcessAvailableAsync(int maxMessages, CancellationToken cancellationToken)
    {
        if (maxMessages <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessages), maxMessages, "Maximum batch size must be greater than zero.");
        }

        if (outboxMessageStore?.SupportsPolling != true || inboxStore is null)
        {
            return new ProcessProjectionOutboxProcessingResult(0, false);
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
        return new ProcessProjectionOutboxProcessingResult(processedCount, hasRemainingMessages);
    }

    private static void ValidateMessage(OutboxMessage message)
    {
        if (string.Equals(message.MessageType, typeof(LegislativeProcessStartedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            _ = JsonSerializer.Deserialize<LegislativeProcessStartedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(LegislativeProcessStartedIntegrationEvent)}'.");
            return;
        }

        if (string.Equals(message.MessageType, typeof(LegislativeStageRecordedIntegrationEvent).FullName, StringComparison.Ordinal))
        {
            _ = JsonSerializer.Deserialize<LegislativeStageRecordedIntegrationEvent>(message.Payload, SerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(LegislativeStageRecordedIntegrationEvent)}'.");
            return;
        }

        throw new InvalidOperationException($"Unsupported outbox message type '{message.MessageType}' for '{nameof(ProcessProjectionOutboxProcessor)}'.");
    }
}
