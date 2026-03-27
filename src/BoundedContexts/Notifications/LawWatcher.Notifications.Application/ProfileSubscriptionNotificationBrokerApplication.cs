using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.TaxonomyAndProfiles.Contracts;

namespace LawWatcher.Notifications.Application;

public sealed class ProfileSubscriptionNotificationMessageHandler(
    AlertNotificationDispatchService alertNotificationDispatchService,
    IInboxStore inboxStore)
{
    public const string ConsumerName = "worker-lite.profile-subscription-notification-dispatch";

    public Task<AlertNotificationDispatchResult> HandleAsync(
        ProfileSubscriptionCreatedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return HandleCoreAsync(integrationEvent.EventId, cancellationToken);
    }

    public Task<AlertNotificationDispatchResult> HandleAsync(
        ProfileSubscriptionAlertPolicyChangedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return HandleCoreAsync(integrationEvent.EventId, cancellationToken);
    }

    public Task<AlertNotificationDispatchResult> HandleAsync(
        ProfileSubscriptionDeactivatedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return HandleCoreAsync(integrationEvent.EventId, cancellationToken);
    }

    private async Task<AlertNotificationDispatchResult> HandleCoreAsync(Guid eventId, CancellationToken cancellationToken)
    {
        if (await inboxStore.HasProcessedAsync(eventId, ConsumerName, cancellationToken))
        {
            return new AlertNotificationDispatchResult(0, 0);
        }

        var result = await alertNotificationDispatchService.DispatchPendingAsync(cancellationToken);
        await inboxStore.MarkProcessedAsync(eventId, ConsumerName, cancellationToken);
        return result;
    }
}
