using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.TaxonomyAndProfiles.Application;
using LawWatcher.TaxonomyAndProfiles.Domain.Subscriptions;

namespace LawWatcher.TaxonomyAndProfiles.Infrastructure;

public sealed class InMemoryProfileSubscriptionRepository : IProfileSubscriptionRepository
{
    private readonly Dictionary<string, List<IDomainEvent>> _streams = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<ProfileSubscription?> GetAsync(ProfileSubscriptionId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_streams.TryGetValue(GetStreamId(id), out var history))
            {
                return Task.FromResult<ProfileSubscription?>(null);
            }

            return Task.FromResult<ProfileSubscription?>(ProfileSubscription.Rehydrate(history.ToArray()));
        }
    }

    public Task SaveAsync(ProfileSubscription subscription, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pendingEvents = subscription.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return Task.CompletedTask;
        }

        var streamId = GetStreamId(subscription.Id);
        var expectedVersion = subscription.Version - pendingEvents.Length;

        lock (_gate)
        {
            if (!_streams.TryGetValue(streamId, out var history))
            {
                history = [];
                _streams.Add(streamId, history);
            }

            if (history.Count != expectedVersion)
            {
                throw new InvalidOperationException($"Optimistic concurrency violation for stream '{streamId}'.");
            }

            history.AddRange(pendingEvents);
        }

        subscription.DequeueUncommittedEvents();
        return Task.CompletedTask;
    }

    private static string GetStreamId(ProfileSubscriptionId id) => $"taxonomy-profile-subscription-{id.Value:D}";
}

public sealed class InMemoryProfileSubscriptionProjectionStore : IProfileSubscriptionReadRepository, IProfileSubscriptionProjection
{
    private readonly Dictionary<Guid, ProjectionState> _subscriptions = new();
    private readonly Lock _gate = new();

    public Task<IReadOnlyCollection<ProfileSubscriptionReadModel>> GetSubscriptionsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyCollection<ProfileSubscriptionReadModel>>(
                _subscriptions.Values
                    .Select(state => state.ToReadModel())
                    .ToArray());
        }
    }

    public Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            foreach (var domainEvent in domainEvents)
            {
                switch (domainEvent)
                {
                    case ProfileSubscriptionCreated created:
                        _subscriptions[created.SubscriptionId.Value] = ProjectionState.From(created);
                        break;
                    case ProfileSubscriptionAlertPolicyChanged changed when _subscriptions.TryGetValue(changed.SubscriptionId.Value, out var existing):
                        existing.ChangeAlertPolicy(changed.AlertPolicyCode, changed.DigestInterval);
                        break;
                    case ProfileSubscriptionDeactivated deactivated:
                        _subscriptions.Remove(deactivated.SubscriptionId.Value);
                        break;
                }
            }
        }

        return Task.CompletedTask;
    }

    private sealed class ProjectionState
    {
        private ProjectionState(
            Guid id,
            Guid profileId,
            string profileName,
            string subscriber,
            string channel,
            string alertPolicy,
            TimeSpan? digestInterval)
        {
            Id = id;
            ProfileId = profileId;
            ProfileName = profileName;
            Subscriber = subscriber;
            Channel = channel;
            AlertPolicy = alertPolicy;
            DigestInterval = digestInterval;
        }

        public Guid Id { get; }

        public Guid ProfileId { get; }

        public string ProfileName { get; }

        public string Subscriber { get; }

        public string Channel { get; }

        public string AlertPolicy { get; private set; }

        public TimeSpan? DigestInterval { get; private set; }

        public static ProjectionState From(ProfileSubscriptionCreated created)
        {
            return new ProjectionState(
                created.SubscriptionId.Value,
                created.ProfileId,
                created.ProfileName,
                created.Subscriber,
                created.ChannelCode,
                created.AlertPolicyCode,
                created.DigestInterval);
        }

        public void ChangeAlertPolicy(string alertPolicy, TimeSpan? digestInterval)
        {
            AlertPolicy = alertPolicy;
            DigestInterval = digestInterval;
        }

        public ProfileSubscriptionReadModel ToReadModel()
        {
            return new ProfileSubscriptionReadModel(
                Id,
                ProfileId,
                ProfileName,
                Subscriber,
                Channel,
                AlertPolicy,
                DigestInterval);
        }
    }
}
