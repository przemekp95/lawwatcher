using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.Notifications.Application;
using LawWatcher.Notifications.Domain.BillAlerts;

namespace LawWatcher.Notifications.Infrastructure;

public sealed class FileBackedBillAlertRepository(string rootPath) : IBillAlertRepository
{
    private readonly string _rootPath = rootPath;
    private readonly string _streamsDirectory = Path.Combine(rootPath, "streams");
    private readonly string _pairsPath = Path.Combine(rootPath, "pairs.json");

    public Task<bool> ExistsAsync(Guid profileId, Guid billId, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var pairs = await JsonFilePersistence.LoadAsync(
                    _pairsPath,
                    () => Array.Empty<string>(),
                    ct);

                return pairs.Contains(GetPairKey(profileId, billId), StringComparer.OrdinalIgnoreCase);
            },
            cancellationToken);
    }

    public Task SaveAsync(BillAlert alert, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var pendingEvents = alert.UncommittedEvents.ToArray();
                if (pendingEvents.Length == 0)
                {
                    return;
                }

                var pairKey = GetPairKey(alert.ProfileId, alert.BillId);
                var pairs = (await JsonFilePersistence.LoadAsync(
                    _pairsPath,
                    () => Array.Empty<string>(),
                    ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (pairs.Contains(pairKey))
                {
                    throw new InvalidOperationException($"Alert for pair '{pairKey}' already exists.");
                }

                var streamPath = GetStreamPath(alert.Id);
                var document = await JsonFilePersistence.LoadAsync(
                    streamPath,
                    () => new BillAlertStreamDocument([]),
                    ct);

                var expectedVersion = alert.Version - pendingEvents.Length;
                if (document.Events.Length != expectedVersion)
                {
                    throw new InvalidOperationException($"Optimistic concurrency violation for alert stream '{alert.Id.Value:D}'.");
                }

                await JsonFilePersistence.SaveAsync(
                    streamPath,
                    new BillAlertStreamDocument(document.Events.Concat(pendingEvents.Select(FromDomainEvent)).ToArray()),
                    ct);

                pairs.Add(pairKey);
                await JsonFilePersistence.SaveAsync(_pairsPath, pairs.OrderBy(pair => pair, StringComparer.OrdinalIgnoreCase).ToArray(), ct);

                alert.DequeueUncommittedEvents();
            },
            cancellationToken);
    }

    private string GetStreamPath(AlertId id) => Path.Combine(_streamsDirectory, $"{id.Value:D}.json");

    private static BillAlertEventRecord FromDomainEvent(IDomainEvent domainEvent) =>
        domainEvent switch
        {
            BillAlertCreated created => new BillAlertEventRecord(
                created.AlertId.Value,
                created.ProfileId,
                created.ProfileName,
                created.BillId,
                created.BillTitle,
                created.BillExternalId,
                created.BillSubmittedOn,
                created.AlertPolicy,
                created.MatchedKeywords.ToArray(),
                created.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported bill alert domain event type '{domainEvent.GetType().Name}'.")
        };

    private static IDomainEvent ToDomainEvent(BillAlertEventRecord record) =>
        new BillAlertCreated(
            new AlertId(record.AlertId),
            record.ProfileId,
            record.ProfileName,
            record.BillId,
            record.BillTitle,
            record.BillExternalId,
            record.BillSubmittedOn,
            record.AlertPolicy,
            record.MatchedKeywords,
            record.OccurredAtUtc);

    private static string GetPairKey(Guid profileId, Guid billId) => $"{profileId:D}:{billId:D}";

    private sealed record BillAlertStreamDocument(BillAlertEventRecord[] Events);

    private sealed record BillAlertEventRecord(
        Guid AlertId,
        Guid ProfileId,
        string ProfileName,
        Guid BillId,
        string BillTitle,
        string BillExternalId,
        DateOnly BillSubmittedOn,
        string AlertPolicy,
        string[] MatchedKeywords,
        DateTimeOffset OccurredAtUtc);
}

public sealed class FileBackedBillAlertProjectionStore(string rootPath) : IBillAlertReadRepository, IBillAlertProjection
{
    private readonly string _rootPath = rootPath;
    private readonly string _projectionPath = Path.Combine(rootPath, "projection.json");

    public Task<IReadOnlyCollection<BillAlertReadModel>> GetAlertsAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new BillAlertProjectionDocument([]),
                    ct);

                return (IReadOnlyCollection<BillAlertReadModel>)document.Alerts.ToArray();
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
                    () => new BillAlertProjectionDocument([]),
                    ct);

                var alerts = document.Alerts.ToDictionary(alert => alert.Id);
                foreach (var domainEvent in domainEvents)
                {
                    if (domainEvent is not BillAlertCreated created)
                    {
                        continue;
                    }

                    alerts[created.AlertId.Value] = new BillAlertReadModel(
                        created.AlertId.Value,
                        created.ProfileId,
                        created.ProfileName,
                        created.BillId,
                        created.BillTitle,
                        created.BillExternalId,
                        created.BillSubmittedOn,
                        created.AlertPolicy,
                        created.MatchedKeywords.ToArray(),
                        created.OccurredAtUtc);
                }

                await JsonFilePersistence.SaveAsync(
                    _projectionPath,
                    new BillAlertProjectionDocument(alerts.Values.ToArray()),
                    ct);
            },
            cancellationToken);
    }

    private sealed record BillAlertProjectionDocument(BillAlertReadModel[] Alerts);
}
