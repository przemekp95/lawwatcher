using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.TaxonomyAndProfiles.Contracts;
using LawWatcher.TaxonomyAndProfiles.Domain.MonitoringProfiles;
using LawWatcher.TaxonomyAndProfiles.Domain.Subscriptions;

namespace LawWatcher.TaxonomyAndProfiles.Application;

public sealed record CreateProfileSubscriptionCommand(
    Guid SubscriptionId,
    Guid ProfileId,
    string ProfileName,
    string Subscriber,
    SubscriptionChannel Channel,
    AlertPolicy AlertPolicy) : Command;

public sealed record ChangeProfileSubscriptionAlertPolicyCommand(
    Guid SubscriptionId,
    AlertPolicy AlertPolicy) : Command;

public sealed record DeactivateProfileSubscriptionCommand(Guid SubscriptionId) : Command;

public sealed record ProfileSubscriptionReadModel(
    Guid Id,
    Guid ProfileId,
    string ProfileName,
    string Subscriber,
    string Channel,
    string AlertPolicy,
    TimeSpan? DigestInterval);

public interface IProfileSubscriptionRepository
{
    Task<ProfileSubscription?> GetAsync(ProfileSubscriptionId id, CancellationToken cancellationToken);

    Task SaveAsync(ProfileSubscription subscription, CancellationToken cancellationToken);
}

public interface IProfileSubscriptionOutboxWriter
{
    Task SaveAsync(
        ProfileSubscription subscription,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken);
}

public interface IProfileSubscriptionReadRepository
{
    Task<IReadOnlyCollection<ProfileSubscriptionReadModel>> GetSubscriptionsAsync(CancellationToken cancellationToken);
}

public interface IProfileSubscriptionProjection
{
    Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}

public sealed class ProfileSubscriptionsCommandService(
    IProfileSubscriptionRepository repository,
    IProfileSubscriptionProjection projection)
{
    public async Task CreateAsync(CreateProfileSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var subscriptionId = new ProfileSubscriptionId(command.SubscriptionId);
        var existing = await repository.GetAsync(subscriptionId, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Profile subscription '{command.SubscriptionId}' has already been created.");
        }

        var subscription = ProfileSubscription.Create(
            subscriptionId,
            SubscribedProfileReference.Create(command.ProfileId, command.ProfileName),
            SubscriberAddress.Create(command.Subscriber),
            command.Channel,
            command.AlertPolicy,
            command.RequestedAtUtc);

        await SaveAndProjectAsync(subscription, cancellationToken);
    }

    public async Task ChangeAlertPolicyAsync(ChangeProfileSubscriptionAlertPolicyCommand command, CancellationToken cancellationToken)
    {
        var subscription = await repository.GetAsync(new ProfileSubscriptionId(command.SubscriptionId), cancellationToken)
            ?? throw new InvalidOperationException($"Profile subscription '{command.SubscriptionId}' was not found.");

        subscription.ChangeAlertPolicy(command.AlertPolicy, command.RequestedAtUtc);
        await SaveAndProjectAsync(subscription, cancellationToken);
    }

    public async Task DeactivateAsync(DeactivateProfileSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var subscription = await repository.GetAsync(new ProfileSubscriptionId(command.SubscriptionId), cancellationToken)
            ?? throw new InvalidOperationException($"Profile subscription '{command.SubscriptionId}' was not found.");

        subscription.Deactivate(command.RequestedAtUtc);
        await SaveAndProjectAsync(subscription, cancellationToken);
    }

    private async Task SaveAndProjectAsync(ProfileSubscription subscription, CancellationToken cancellationToken)
    {
        var pendingEvents = subscription.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var integrationEvents = new List<IIntegrationEvent>(pendingEvents.Length);
        foreach (var domainEvent in pendingEvents)
        {
            switch (domainEvent)
            {
                case ProfileSubscriptionCreated created:
                    integrationEvents.Add(new ProfileSubscriptionCreatedIntegrationEvent(
                        created.EventId,
                        created.OccurredAtUtc,
                        created.SubscriptionId.Value,
                        created.ProfileId,
                        created.ProfileName,
                        created.Subscriber,
                        created.ChannelCode,
                        created.AlertPolicyCode,
                        created.DigestInterval));
                    break;
                case ProfileSubscriptionAlertPolicyChanged changed:
                    integrationEvents.Add(new ProfileSubscriptionAlertPolicyChangedIntegrationEvent(
                        changed.EventId,
                        changed.OccurredAtUtc,
                        changed.SubscriptionId.Value,
                        changed.AlertPolicyCode,
                        changed.DigestInterval));
                    break;
                case ProfileSubscriptionDeactivated deactivated:
                    integrationEvents.Add(new ProfileSubscriptionDeactivatedIntegrationEvent(
                        deactivated.EventId,
                        deactivated.OccurredAtUtc,
                        deactivated.SubscriptionId.Value));
                    break;
            }
        }

        if (repository is IProfileSubscriptionOutboxWriter outboxWriter && integrationEvents.Count != 0)
        {
            await outboxWriter.SaveAsync(subscription, integrationEvents, cancellationToken);
        }
        else
        {
            await repository.SaveAsync(subscription, cancellationToken);
        }

        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }
}

public sealed class ProfileSubscriptionsQueryService(IProfileSubscriptionReadRepository repository)
{
    public async Task<IReadOnlyList<ProfileSubscriptionResponse>> GetSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var subscriptions = await repository.GetSubscriptionsAsync(cancellationToken);

        return subscriptions
            .OrderBy(subscription => subscription.Subscriber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(subscription => subscription.ProfileName, StringComparer.OrdinalIgnoreCase)
            .Select(subscription => new ProfileSubscriptionResponse(
                subscription.Id,
                subscription.ProfileId,
                subscription.ProfileName,
                subscription.Subscriber,
                subscription.Channel,
                subscription.AlertPolicy,
                subscription.DigestInterval.HasValue ? Convert.ToInt32(subscription.DigestInterval.Value.TotalMinutes) : null))
            .ToArray();
    }
}
