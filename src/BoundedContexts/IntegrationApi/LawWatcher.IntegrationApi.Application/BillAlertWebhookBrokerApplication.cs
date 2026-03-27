using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.Notifications.Contracts;

namespace LawWatcher.IntegrationApi.Application;

public sealed class BillAlertWebhookMessageHandler(
    AlertWebhookDispatchService alertWebhookDispatchService,
    IInboxStore inboxStore)
{
    public const string ConsumerName = "worker-lite.bill-alert-webhook-dispatch";

    public async Task<AlertWebhookDispatchResult> HandleAsync(
        BillAlertCreatedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        if (await inboxStore.HasProcessedAsync(integrationEvent.EventId, ConsumerName, cancellationToken))
        {
            return new AlertWebhookDispatchResult(0);
        }

        var result = await alertWebhookDispatchService.DispatchAlertAsync(ToReadModel(integrationEvent), cancellationToken);
        await inboxStore.MarkProcessedAsync(integrationEvent.EventId, ConsumerName, cancellationToken);
        return result;
    }

    private static WebhookAlertReadModel ToReadModel(BillAlertCreatedIntegrationEvent integrationEvent)
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
