using LawWatcher.AiEnrichment.Contracts;
using LawWatcher.AiEnrichment.Domain.Tasks;
using LawWatcher.BuildingBlocks.Messaging;
using System.Text.Json;

namespace LawWatcher.AiEnrichment.Application;

public sealed record IntegrationEventPublishingBatchResult(
    int PublishedCount,
    bool HasRemainingMessages);

public sealed class AiEnrichmentRequestedOutboxPublisher(
    IOutboxMessageStore outboxMessageStore,
    IIntegrationEventPublisher integrationEventPublisher)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] RequestedMessageTypes = [typeof(AiEnrichmentRequestedIntegrationEvent).FullName ?? nameof(AiEnrichmentRequestedIntegrationEvent)];

    public async Task<IntegrationEventPublishingBatchResult> PublishPendingAsync(int maxMessages, CancellationToken cancellationToken)
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
                var integrationEvent = JsonSerializer.Deserialize<AiEnrichmentRequestedIntegrationEvent>(message.Payload, SerializerOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(AiEnrichmentRequestedIntegrationEvent)}'.");

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
        return new IntegrationEventPublishingBatchResult(publishedCount, hasRemainingMessages);
    }
}

public sealed class AiEnrichmentRequestedMessageHandler(
    AiEnrichmentExecutionService executionService,
    IInboxStore inboxStore)
{
    public async Task<AiEnrichmentExecutionResult> HandleAsync(
        AiEnrichmentRequestedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        if (await inboxStore.HasProcessedAsync(integrationEvent.EventId, AiEnrichmentQueueProcessor.ConsumerName, cancellationToken))
        {
            return new AiEnrichmentExecutionResult(false, integrationEvent.TaskId, null);
        }

        var result = await executionService.ProcessAsync(new AiEnrichmentTaskId(integrationEvent.TaskId), cancellationToken);
        await inboxStore.MarkProcessedAsync(integrationEvent.EventId, AiEnrichmentQueueProcessor.ConsumerName, cancellationToken);
        return result;
    }
}
