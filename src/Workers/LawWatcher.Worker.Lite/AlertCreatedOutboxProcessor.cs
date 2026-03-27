using System.Text.Json;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.Notifications.Application;
using LawWatcher.Notifications.Contracts;

namespace LawWatcher.Worker.Lite;

public sealed record AlertCreatedOutboxProcessingResult(
    int ProcessedCount,
    bool HasRemainingMessages);

public sealed class AlertCreatedOutboxProcessor(
    AlertNotificationDispatchService alertNotificationDispatchService,
    AlertWebhookDispatchService alertWebhookDispatchService,
    IOutboxMessageStore? outboxMessageStore = null,
    IInboxStore? inboxStore = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] RequestedMessageTypes = [typeof(BillAlertCreatedIntegrationEvent).FullName ?? nameof(BillAlertCreatedIntegrationEvent)];
    public const string ConsumerName = "worker-lite.alert-created";

    public async Task<AlertCreatedOutboxProcessingResult> ProcessAvailableAsync(int maxTasks, CancellationToken cancellationToken)
    {
        if (maxTasks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTasks), maxTasks, "Maximum batch size must be greater than zero.");
        }

        if (outboxMessageStore?.SupportsPolling != true || inboxStore is null)
        {
            return new AlertCreatedOutboxProcessingResult(0, false);
        }

        var messages = await outboxMessageStore.GetPendingAsync(RequestedMessageTypes, maxTasks, cancellationToken);
        var processedCount = 0;

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
                var integrationEvent = JsonSerializer.Deserialize<BillAlertCreatedIntegrationEvent>(message.Payload, SerializerOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(BillAlertCreatedIntegrationEvent)}'.");

                await alertNotificationDispatchService.DispatchAlertAsync(ToNotificationAlert(integrationEvent), cancellationToken);
                await alertWebhookDispatchService.DispatchAlertAsync(ToWebhookAlert(integrationEvent), cancellationToken);
                await inboxStore.MarkProcessedAsync(message.MessageId, ConsumerName, cancellationToken);
                await outboxMessageStore.MarkPublishedAsync(message.MessageId, DateTimeOffset.UtcNow, cancellationToken);
                processedCount++;
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
        return new AlertCreatedOutboxProcessingResult(processedCount, hasRemainingMessages);
    }

    private static BillAlertReadModel ToNotificationAlert(BillAlertCreatedIntegrationEvent integrationEvent)
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

    private static WebhookAlertReadModel ToWebhookAlert(BillAlertCreatedIntegrationEvent integrationEvent)
    {
        return new WebhookAlertReadModel(
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
