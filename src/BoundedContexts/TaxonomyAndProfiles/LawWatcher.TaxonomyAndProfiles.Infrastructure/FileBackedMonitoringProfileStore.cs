using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.TaxonomyAndProfiles.Application;
using LawWatcher.TaxonomyAndProfiles.Domain.MonitoringProfiles;

namespace LawWatcher.TaxonomyAndProfiles.Infrastructure;

public sealed class FileBackedMonitoringProfileRepository(string rootPath) : IMonitoringProfileRepository
{
    private readonly string _rootPath = rootPath;
    private readonly string _streamsDirectory = Path.Combine(rootPath, "streams");

    public Task<MonitoringProfile?> GetAsync(MonitoringProfileId id, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    GetStreamPath(id),
                    () => new MonitoringProfileStreamDocument([]),
                    ct);

                return document.Events.Length == 0
                    ? null
                    : MonitoringProfile.Rehydrate(document.Events.Select(ToDomainEvent).ToArray());
            },
            cancellationToken);
    }

    public Task SaveAsync(MonitoringProfile profile, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var pendingEvents = profile.UncommittedEvents.ToArray();
                if (pendingEvents.Length == 0)
                {
                    return;
                }

                var streamPath = GetStreamPath(profile.Id);
                var document = await JsonFilePersistence.LoadAsync(
                    streamPath,
                    () => new MonitoringProfileStreamDocument([]),
                    ct);

                var expectedVersion = profile.Version - pendingEvents.Length;
                if (document.Events.Length != expectedVersion)
                {
                    throw new InvalidOperationException($"Optimistic concurrency violation for monitoring profile stream '{profile.Id.Value:D}'.");
                }

                await JsonFilePersistence.SaveAsync(
                    streamPath,
                    new MonitoringProfileStreamDocument(document.Events.Concat(pendingEvents.Select(FromDomainEvent)).ToArray()),
                    ct);

                profile.DequeueUncommittedEvents();
            },
            cancellationToken);
    }

    private string GetStreamPath(MonitoringProfileId id) => Path.Combine(_streamsDirectory, $"{id.Value:D}.json");

    private static MonitoringProfileEventRecord FromDomainEvent(IDomainEvent domainEvent) =>
        domainEvent switch
        {
            MonitoringProfileCreated created => new MonitoringProfileEventRecord(
                "created",
                created.ProfileId.Value,
                created.Name,
                created.AlertPolicyCode,
                created.DigestInterval?.Ticks,
                null,
                null,
                created.OccurredAtUtc),
            MonitoringProfileRuleAdded added => new MonitoringProfileEventRecord(
                "rule-added",
                added.ProfileId.Value,
                null,
                null,
                null,
                added.RuleKind,
                added.RuleValue,
                added.OccurredAtUtc),
            MonitoringProfileAlertPolicyChanged changed => new MonitoringProfileEventRecord(
                "alert-policy-changed",
                changed.ProfileId.Value,
                null,
                changed.AlertPolicyCode,
                changed.DigestInterval?.Ticks,
                null,
                null,
                changed.OccurredAtUtc),
            MonitoringProfileDeactivated deactivated => new MonitoringProfileEventRecord(
                "deactivated",
                deactivated.ProfileId.Value,
                null,
                null,
                null,
                null,
                null,
                deactivated.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported monitoring profile domain event type '{domainEvent.GetType().Name}'.")
        };

    private static IDomainEvent ToDomainEvent(MonitoringProfileEventRecord record) =>
        record.Type switch
        {
            "created" => new MonitoringProfileCreated(
                new MonitoringProfileId(record.ProfileId),
                record.Name ?? throw new InvalidOperationException("Monitoring profile created event is missing name."),
                record.AlertPolicyCode ?? throw new InvalidOperationException("Monitoring profile created event is missing alert policy."),
                record.DigestIntervalTicks is null ? null : TimeSpan.FromTicks(record.DigestIntervalTicks.Value),
                record.OccurredAtUtc),
            "rule-added" => new MonitoringProfileRuleAdded(
                new MonitoringProfileId(record.ProfileId),
                record.RuleKind ?? throw new InvalidOperationException("Monitoring profile rule-added event is missing rule kind."),
                record.RuleValue ?? throw new InvalidOperationException("Monitoring profile rule-added event is missing rule value."),
                record.OccurredAtUtc),
            "alert-policy-changed" => new MonitoringProfileAlertPolicyChanged(
                new MonitoringProfileId(record.ProfileId),
                record.AlertPolicyCode ?? throw new InvalidOperationException("Monitoring profile alert-policy-changed event is missing alert policy."),
                record.DigestIntervalTicks is null ? null : TimeSpan.FromTicks(record.DigestIntervalTicks.Value),
                record.OccurredAtUtc),
            "deactivated" => new MonitoringProfileDeactivated(
                new MonitoringProfileId(record.ProfileId),
                record.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported monitoring profile event record type '{record.Type}'.")
        };

    private sealed record MonitoringProfileStreamDocument(MonitoringProfileEventRecord[] Events);

    private sealed record MonitoringProfileEventRecord(
        string Type,
        Guid ProfileId,
        string? Name,
        string? AlertPolicyCode,
        long? DigestIntervalTicks,
        string? RuleKind,
        string? RuleValue,
        DateTimeOffset OccurredAtUtc);
}

public sealed class FileBackedMonitoringProfileProjectionStore(string rootPath) : IMonitoringProfileReadRepository, IMonitoringProfileProjection
{
    private readonly string _rootPath = rootPath;
    private readonly string _projectionPath = Path.Combine(rootPath, "projection.json");

    public Task<IReadOnlyCollection<MonitoringProfileReadModel>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new MonitoringProfileProjectionDocument([]),
                    ct);

                return (IReadOnlyCollection<MonitoringProfileReadModel>)document.Profiles
                    .Select(record => new MonitoringProfileReadModel(record.Id, record.Name, record.AlertPolicy, record.Keywords))
                    .ToArray();
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
                    () => new MonitoringProfileProjectionDocument([]),
                    ct);

                var profiles = document.Profiles.ToDictionary(record => record.Id);
                foreach (var domainEvent in domainEvents)
                {
                    switch (domainEvent)
                    {
                        case MonitoringProfileCreated created:
                            profiles[created.ProfileId.Value] = new MonitoringProfileProjectionRecord(
                                created.ProfileId.Value,
                                created.Name,
                                created.AlertPolicyCode,
                                []);
                            break;
                        case MonitoringProfileRuleAdded added when profiles.TryGetValue(added.ProfileId.Value, out var profile):
                            if (!string.Equals(added.RuleKind, "keyword", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            profiles[added.ProfileId.Value] = profile with
                            {
                                Keywords = profile.Keywords
                                    .Append(added.RuleValue)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(keyword => keyword, StringComparer.OrdinalIgnoreCase)
                                    .ToArray()
                            };
                            break;
                        case MonitoringProfileAlertPolicyChanged changed when profiles.TryGetValue(changed.ProfileId.Value, out var changedProfile):
                            profiles[changed.ProfileId.Value] = changedProfile with
                            {
                                AlertPolicy = changed.AlertPolicyCode
                            };
                            break;
                        case MonitoringProfileDeactivated deactivated:
                            profiles.Remove(deactivated.ProfileId.Value);
                            break;
                    }
                }

                await JsonFilePersistence.SaveAsync(
                    _projectionPath,
                    new MonitoringProfileProjectionDocument(
                        profiles.Values
                            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                            .ToArray()),
                    ct);
            },
            cancellationToken);
    }

    private sealed record MonitoringProfileProjectionDocument(MonitoringProfileProjectionRecord[] Profiles);

    private sealed record MonitoringProfileProjectionRecord(
        Guid Id,
        string Name,
        string AlertPolicy,
        string[] Keywords);
}
