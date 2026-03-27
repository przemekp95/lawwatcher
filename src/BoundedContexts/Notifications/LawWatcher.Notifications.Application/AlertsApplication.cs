using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.BuildingBlocks.Ports;
using LawWatcher.LegislativeIntake.Contracts;
using LawWatcher.Notifications.Contracts;
using LawWatcher.Notifications.Domain.BillAlerts;
using LawWatcher.TaxonomyAndProfiles.Contracts;

namespace LawWatcher.Notifications.Application;

public sealed record BillAlertReadModel(
    Guid Id,
    Guid ProfileId,
    string ProfileName,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    DateOnly BillSubmittedOn,
    string AlertPolicy,
    IReadOnlyCollection<string> MatchedKeywords,
    DateTimeOffset CreatedAtUtc);

public interface IBillAlertRepository
{
    Task<bool> ExistsAsync(Guid profileId, Guid billId, CancellationToken cancellationToken);

    Task SaveAsync(BillAlert alert, CancellationToken cancellationToken);
}

public interface IBillAlertOutboxWriter
{
    Task SaveAsync(
        BillAlert alert,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken);
}

public interface IBillAlertReadRepository
{
    Task<IReadOnlyCollection<BillAlertReadModel>> GetAlertsAsync(CancellationToken cancellationToken);
}

public interface IBillAlertProjection
{
    Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}

public sealed record AlertNotificationDispatchReadModel(
    Guid AlertId,
    Guid SubscriptionId,
    string ProfileName,
    string BillTitle,
    string Channel,
    string Recipient,
    DateTimeOffset DispatchedAtUtc);

public interface IAlertNotificationDispatchStore
{
    Task<bool> HasDispatchedAsync(Guid alertId, Guid subscriptionId, CancellationToken cancellationToken);

    Task SaveAsync(AlertNotificationDispatchReadModel dispatch, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<AlertNotificationDispatchReadModel>> GetDispatchesAsync(CancellationToken cancellationToken);
}

public sealed record NotificationSubscriptionReadModel(
    Guid Id,
    Guid ProfileId,
    string ProfileName,
    string Subscriber,
    string Channel,
    string AlertPolicy,
    TimeSpan? DigestInterval);

public interface INotificationSubscriptionReadRepository
{
    Task<IReadOnlyCollection<NotificationSubscriptionReadModel>> GetSubscriptionsAsync(CancellationToken cancellationToken);
}

public sealed class AlertGenerationService(
    IBillAlertRepository repository,
    IBillAlertProjection projection)
{
    public async Task GenerateAlertsAsync(
        IReadOnlyCollection<BillSummaryResponse> bills,
        IReadOnlyCollection<MonitoringProfileResponse> profiles,
        DateTimeOffset generatedAtUtc,
        CancellationToken cancellationToken)
    {
        foreach (var bill in bills)
        {
            foreach (var profile in profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var matchedKeywords = MatchKeywords(profile, bill);
                if (matchedKeywords.Length == 0)
                {
                    continue;
                }

                if (await repository.ExistsAsync(profile.Id, bill.Id, cancellationToken))
                {
                    continue;
                }

                var alert = BillAlert.Create(
                    new AlertId(Guid.NewGuid()),
                    profile.Id,
                    profile.Name,
                    bill.Id,
                    bill.Title,
                    bill.ExternalId,
                    bill.SubmittedOn,
                    AlertPolicySnapshot.Create(profile.AlertPolicy),
                    matchedKeywords,
                    generatedAtUtc);

                await SaveAndProjectAsync(alert, cancellationToken);
            }
        }
    }

    private async Task SaveAndProjectAsync(BillAlert alert, CancellationToken cancellationToken)
    {
        var pendingEvents = alert.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var integrationEvents = pendingEvents
            .OfType<BillAlertCreated>()
            .Select(created => new BillAlertCreatedIntegrationEvent(
                created.EventId,
                created.OccurredAtUtc,
                created.AlertId.Value,
                created.ProfileId,
                created.ProfileName,
                created.BillId,
                created.BillTitle,
                created.BillExternalId,
                created.BillSubmittedOn,
                created.AlertPolicy,
                created.MatchedKeywords.ToArray()))
            .Cast<IIntegrationEvent>()
            .ToArray();

        if (repository is IBillAlertOutboxWriter outboxWriter && integrationEvents.Length != 0)
        {
            await outboxWriter.SaveAsync(alert, integrationEvents, cancellationToken);
        }
        else
        {
            await repository.SaveAsync(alert, cancellationToken);
        }

        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }

    private static string[] MatchKeywords(MonitoringProfileResponse profile, BillSummaryResponse bill)
    {
        return profile.Keywords
            .Where(keyword => bill.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(keyword => keyword, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed class AlertsQueryService(IBillAlertReadRepository repository)
{
    public async Task<IReadOnlyList<BillAlertResponse>> GetAlertsAsync(CancellationToken cancellationToken)
    {
        var alerts = await repository.GetAlertsAsync(cancellationToken);

        return alerts
            .OrderByDescending(alert => alert.BillSubmittedOn)
            .ThenBy(alert => alert.ProfileName, StringComparer.OrdinalIgnoreCase)
            .Select(alert => new BillAlertResponse(
                alert.Id,
                alert.ProfileId,
                alert.ProfileName,
                alert.BillId,
                alert.BillTitle,
                alert.BillExternalId,
                alert.BillSubmittedOn,
                alert.AlertPolicy,
                alert.MatchedKeywords.ToArray(),
                alert.CreatedAtUtc))
            .ToArray();
    }
}

public sealed record AlertNotificationDispatchResult(
    int ProcessedCount,
    int SkippedDigestCount);

public sealed class AlertNotificationDispatchService(
    IBillAlertReadRepository alertRepository,
    INotificationSubscriptionReadRepository subscriptionRepository,
    IEnumerable<INotificationChannel> channels,
    IAlertNotificationDispatchStore dispatchStore)
{
    public async Task<AlertNotificationDispatchResult> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var alerts = await alertRepository.GetAlertsAsync(cancellationToken);
        var subscriptions = await subscriptionRepository.GetSubscriptionsAsync(cancellationToken);
        var channelMap = channels.ToDictionary(channel => channel.ChannelCode, StringComparer.OrdinalIgnoreCase);

        var processedCount = 0;
        var skippedDigestCount = 0;

        foreach (var alert in alerts.OrderBy(alert => alert.CreatedAtUtc))
        {
            var alertResult = await DispatchAlertAsync(alert, subscriptions, channelMap, cancellationToken);
            processedCount += alertResult.ProcessedCount;
            skippedDigestCount += alertResult.SkippedDigestCount;
        }

        return new AlertNotificationDispatchResult(processedCount, skippedDigestCount);
    }

    public async Task<AlertNotificationDispatchResult> DispatchAlertAsync(
        BillAlertReadModel alert,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var subscriptions = await subscriptionRepository.GetSubscriptionsAsync(cancellationToken);
        var channelMap = channels.ToDictionary(channel => channel.ChannelCode, StringComparer.OrdinalIgnoreCase);
        return await DispatchAlertAsync(alert, subscriptions, channelMap, cancellationToken);
    }

    private async Task<AlertNotificationDispatchResult> DispatchAlertAsync(
        BillAlertReadModel alert,
        IReadOnlyCollection<NotificationSubscriptionReadModel> subscriptions,
        IReadOnlyDictionary<string, INotificationChannel> channelMap,
        CancellationToken cancellationToken)
    {
        var processedCount = 0;
        var skippedDigestCount = 0;

        foreach (var subscription in subscriptions.Where(subscription => subscription.ProfileId == alert.ProfileId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!subscription.AlertPolicy.Equals("immediate", StringComparison.OrdinalIgnoreCase))
            {
                skippedDigestCount++;
                continue;
            }

            if (await dispatchStore.HasDispatchedAsync(alert.Id, subscription.Id, cancellationToken))
            {
                continue;
            }

            if (!channelMap.TryGetValue(subscription.Channel, out var channel))
            {
                throw new InvalidOperationException($"Notification channel '{subscription.Channel}' is not configured.");
            }

            var request = CreateDispatchRequest(alert, subscription);
            await channel.DispatchAsync(request, cancellationToken);
            await dispatchStore.SaveAsync(new AlertNotificationDispatchReadModel(
                alert.Id,
                subscription.Id,
                alert.ProfileName,
                alert.BillTitle,
                subscription.Channel,
                subscription.Subscriber,
                DateTimeOffset.UtcNow), cancellationToken);
            processedCount++;
        }

        return new AlertNotificationDispatchResult(processedCount, skippedDigestCount);
    }

    private static NotificationDispatchRequest CreateDispatchRequest(
        BillAlertReadModel alert,
        NotificationSubscriptionReadModel subscription)
    {
        var subject = $"LawWatcher alert: {alert.BillTitle}";
        var content = $"Profil '{alert.ProfileName}' dopasowal projekt '{alert.BillTitle}' ({string.Join(", ", alert.MatchedKeywords)}).";
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alertId"] = alert.Id.ToString("D"),
            ["profileId"] = alert.ProfileId.ToString("D"),
            ["billId"] = alert.BillId.ToString("D"),
            ["profileName"] = alert.ProfileName,
            ["billTitle"] = alert.BillTitle,
            ["billExternalId"] = alert.BillExternalId,
            ["alertPolicy"] = alert.AlertPolicy
        };

        return new NotificationDispatchRequest(
            subscription.Subscriber,
            subject,
            content,
            "notification.alert.created",
            metadata);
    }
}

public sealed class AlertNotificationDispatchesQueryService(IAlertNotificationDispatchStore store)
{
    public async Task<IReadOnlyList<AlertNotificationDispatchResponse>> GetDispatchesAsync(CancellationToken cancellationToken)
    {
        var dispatches = await store.GetDispatchesAsync(cancellationToken);

        return dispatches
            .OrderByDescending(dispatch => dispatch.DispatchedAtUtc)
            .ThenBy(dispatch => dispatch.Channel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(dispatch => dispatch.Recipient, StringComparer.OrdinalIgnoreCase)
            .Select(dispatch => new AlertNotificationDispatchResponse(
                dispatch.AlertId,
                dispatch.SubscriptionId,
                dispatch.ProfileName,
                dispatch.BillTitle,
                dispatch.Channel,
                dispatch.Recipient,
                dispatch.DispatchedAtUtc))
            .ToArray();
    }
}
