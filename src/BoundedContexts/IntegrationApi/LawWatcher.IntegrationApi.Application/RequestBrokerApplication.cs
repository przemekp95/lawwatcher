using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Contracts;
using LawWatcher.IntegrationApi.Domain.Backfills;
using LawWatcher.IntegrationApi.Domain.Replays;
using System.Text.Json;

namespace LawWatcher.IntegrationApi.Application;

public sealed record RequestIntegrationEventPublishingBatchResult(
    int PublishedCount,
    bool HasRemainingMessages);

public sealed class ReplayRequestedOutboxPublisher(
    IOutboxMessageStore outboxMessageStore,
    IIntegrationEventPublisher integrationEventPublisher)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] RequestedMessageTypes = [typeof(ReplayRequestedIntegrationEvent).FullName ?? nameof(ReplayRequestedIntegrationEvent)];

    public async Task<RequestIntegrationEventPublishingBatchResult> PublishPendingAsync(int maxMessages, CancellationToken cancellationToken)
    {
        if (maxMessages <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessages), maxMessages, "Maximum batch size must be greater than zero.");
        }

        var messages = await outboxMessageStore.GetPendingAsync(RequestedMessageTypes, maxMessages, cancellationToken);
        var publishedCount = 0;

        foreach (var message in messages)
        {
            try
            {
                var integrationEvent = JsonSerializer.Deserialize<ReplayRequestedIntegrationEvent>(message.Payload, SerializerOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(ReplayRequestedIntegrationEvent)}'.");

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

        var hasRemainingMessages = (await outboxMessageStore.GetPendingAsync(RequestedMessageTypes, 1, cancellationToken)).Count != 0;
        return new RequestIntegrationEventPublishingBatchResult(publishedCount, hasRemainingMessages);
    }
}

public sealed class ReplayRequestedMessageHandler(
    ReplayExecutionService executionService,
    IInboxStore inboxStore)
{
    public async Task<ReplayExecutionResult> HandleAsync(
        ReplayRequestedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        if (await inboxStore.HasProcessedAsync(integrationEvent.EventId, ReplayQueueProcessor.ConsumerName, cancellationToken))
        {
            return new ReplayExecutionResult(false, integrationEvent.ReplayRequestId, null);
        }

        var result = await executionService.ProcessAsync(new ReplayRequestId(integrationEvent.ReplayRequestId), cancellationToken);
        await inboxStore.MarkProcessedAsync(integrationEvent.EventId, ReplayQueueProcessor.ConsumerName, cancellationToken);
        return result;
    }
}

public sealed class BackfillRequestedOutboxPublisher(
    IOutboxMessageStore outboxMessageStore,
    IIntegrationEventPublisher integrationEventPublisher)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] RequestedMessageTypes = [typeof(BackfillRequestedIntegrationEvent).FullName ?? nameof(BackfillRequestedIntegrationEvent)];

    public async Task<RequestIntegrationEventPublishingBatchResult> PublishPendingAsync(int maxMessages, CancellationToken cancellationToken)
    {
        if (maxMessages <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessages), maxMessages, "Maximum batch size must be greater than zero.");
        }

        var messages = await outboxMessageStore.GetPendingAsync(RequestedMessageTypes, maxMessages, cancellationToken);
        var publishedCount = 0;

        foreach (var message in messages)
        {
            try
            {
                var integrationEvent = JsonSerializer.Deserialize<BackfillRequestedIntegrationEvent>(message.Payload, SerializerOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(BackfillRequestedIntegrationEvent)}'.");

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

        var hasRemainingMessages = (await outboxMessageStore.GetPendingAsync(RequestedMessageTypes, 1, cancellationToken)).Count != 0;
        return new RequestIntegrationEventPublishingBatchResult(publishedCount, hasRemainingMessages);
    }
}

public sealed class BackfillRequestedMessageHandler(
    BackfillExecutionService executionService,
    IInboxStore inboxStore)
{
    public async Task<BackfillExecutionResult> HandleAsync(
        BackfillRequestedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        if (await inboxStore.HasProcessedAsync(integrationEvent.EventId, BackfillQueueProcessor.ConsumerName, cancellationToken))
        {
            return new BackfillExecutionResult(false, integrationEvent.BackfillRequestId, null);
        }

        var result = await executionService.ProcessAsync(new BackfillRequestId(integrationEvent.BackfillRequestId), cancellationToken);
        await inboxStore.MarkProcessedAsync(integrationEvent.EventId, BackfillQueueProcessor.ConsumerName, cancellationToken);
        return result;
    }
}
