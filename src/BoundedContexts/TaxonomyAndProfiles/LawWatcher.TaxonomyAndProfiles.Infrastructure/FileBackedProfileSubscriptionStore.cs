using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.TaxonomyAndProfiles.Application;
using LawWatcher.TaxonomyAndProfiles.Domain.Subscriptions;

namespace LawWatcher.TaxonomyAndProfiles.Infrastructure;

public sealed class FileBackedProfileSubscriptionRepository(string rootPath) : IProfileSubscriptionRepository
{
    private readonly string _rootPath = rootPath;
    private readonly string _streamsDirectory = Path.Combine(rootPath, "streams");

    public Task<ProfileSubscription?> GetAsync(ProfileSubscriptionId id, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    GetStreamPath(id),
                    () => new ProfileSubscriptionStreamDocument([]),
                    ct);

                return document.Events.Length == 0
                    ? null
                    : ProfileSubscription.Rehydrate(document.Events.Select(ToDomainEvent).ToArray());
            },
            cancellationToken);
    }

    public Task SaveAsync(ProfileSubscription subscription, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var pendingEvents = subscription.UncommittedEvents.ToArray();
                if (pendingEvents.Length == 0)
                {
                    return;
                }

                var streamPath = GetStreamPath(subscription.Id);
                var document = await JsonFilePersistence.LoadAsync(
                    streamPath,
                    () => new ProfileSubscriptionStreamDocument([]),
                    ct);

                var expectedVersion = subscription.Version - pendingEvents.Length;
                if (document.Events.Length != expectedVersion)
                {
                    throw new InvalidOperationException($"Optimistic concurrency violation for profile subscription stream '{subscription.Id.Value:D}'.");
                }

                await JsonFilePersistence.SaveAsync(
                    streamPath,
                    new ProfileSubscriptionStreamDocument(document.Events.Concat(pendingEvents.Select(FromDomainEvent)).ToArray()),
                    ct);

                subscription.DequeueUncommittedEvents();
            },
            cancellationToken);
    }

    private string GetStreamPath(ProfileSubscriptionId id) => Path.Combine(_streamsDirectory, $"{id.Value:D}.json");

    private static ProfileSubscriptionEventRecord FromDomainEvent(IDomainEvent domainEvent) =>
        domainEvent switch
        {
            ProfileSubscriptionCreated created => new ProfileSubscriptionEventRecord(
                "created",
                created.SubscriptionId.Value,
                created.ProfileId,
                created.ProfileName,
                created.Subscriber,
                created.ChannelCode,
                created.AlertPolicyCode,
                created.DigestInterval,
                created.OccurredAtUtc),
            ProfileSubscriptionAlertPolicyChanged changed => new ProfileSubscriptionEventRecord(
                "alert-policy-changed",
                changed.SubscriptionId.Value,
                null,
                null,
                null,
                null,
                changed.AlertPolicyCode,
                changed.DigestInterval,
                changed.OccurredAtUtc),
            ProfileSubscriptionDeactivated deactivated => new ProfileSubscriptionEventRecord(
                "deactivated",
                deactivated.SubscriptionId.Value,
                null,
                null,
                null,
                null,
                null,
                null,
                deactivated.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported profile subscription domain event type '{domainEvent.GetType().Name}'.")
        };

    private static IDomainEvent ToDomainEvent(ProfileSubscriptionEventRecord record) =>
        record.Type switch
        {
            "created" => new ProfileSubscriptionCreated(
                new ProfileSubscriptionId(record.SubscriptionId),
                record.ProfileId ?? throw new InvalidOperationException("Subscription created event is missing profile identifier."),
                record.ProfileName ?? throw new InvalidOperationException("Subscription created event is missing profile name."),
                record.Subscriber ?? throw new InvalidOperationException("Subscription created event is missing subscriber."),
                record.ChannelCode ?? throw new InvalidOperationException("Subscription created event is missing channel."),
                record.AlertPolicyCode ?? throw new InvalidOperationException("Subscription created event is missing alert policy."),
                record.DigestInterval,
                record.OccurredAtUtc),
            "alert-policy-changed" => new ProfileSubscriptionAlertPolicyChanged(
                new ProfileSubscriptionId(record.SubscriptionId),
                record.AlertPolicyCode ?? throw new InvalidOperationException("Subscription policy change event is missing alert policy."),
                record.DigestInterval,
                record.OccurredAtUtc),
            "deactivated" => new ProfileSubscriptionDeactivated(
                new ProfileSubscriptionId(record.SubscriptionId),
                record.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported profile subscription event record type '{record.Type}'.")
        };

    private sealed record ProfileSubscriptionStreamDocument(ProfileSubscriptionEventRecord[] Events);

    private sealed record ProfileSubscriptionEventRecord(
        string Type,
        Guid SubscriptionId,
        Guid? ProfileId,
        string? ProfileName,
        string? Subscriber,
        string? ChannelCode,
        string? AlertPolicyCode,
        TimeSpan? DigestInterval,
        DateTimeOffset OccurredAtUtc);
}

public sealed class FileBackedProfileSubscriptionProjectionStore(string rootPath) : IProfileSubscriptionReadRepository, IProfileSubscriptionProjection
{
    private readonly string _rootPath = rootPath;
    private readonly string _projectionPath = Path.Combine(rootPath, "projection.json");

    public Task<IReadOnlyCollection<ProfileSubscriptionReadModel>> GetSubscriptionsAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new ProfileSubscriptionProjectionDocument([]),
                    ct);

                return (IReadOnlyCollection<ProfileSubscriptionReadModel>)document.Subscriptions.ToArray();
            },
            cancellationToken);
    }

    public Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new ProfileSubscriptionProjectionDocument([]),
                    ct);

                var subscriptions = document.Subscriptions.ToDictionary(subscription => subscription.Id);
                foreach (var domainEvent in domainEvents)
                {
                    switch (domainEvent)
                    {
                        case ProfileSubscriptionCreated created:
                            subscriptions[created.SubscriptionId.Value] = new ProfileSubscriptionReadModel(
                                created.SubscriptionId.Value,
                                created.ProfileId,
                                created.ProfileName,
                                created.Subscriber,
                                created.ChannelCode,
                                created.AlertPolicyCode,
                                created.DigestInterval);
                            break;
                        case ProfileSubscriptionAlertPolicyChanged changed when subscriptions.TryGetValue(changed.SubscriptionId.Value, out var existing):
                            subscriptions[changed.SubscriptionId.Value] = existing with
                            {
                                AlertPolicy = changed.AlertPolicyCode,
                                DigestInterval = changed.DigestInterval
                            };
                            break;
                        case ProfileSubscriptionDeactivated deactivated:
                            subscriptions.Remove(deactivated.SubscriptionId.Value);
                            break;
                    }
                }

                await JsonFilePersistence.SaveAsync(
                    _projectionPath,
                    new ProfileSubscriptionProjectionDocument(subscriptions.Values.ToArray()),
                    ct);
            },
            cancellationToken);
    }

    private sealed record ProfileSubscriptionProjectionDocument(ProfileSubscriptionReadModel[] Subscriptions);
}
