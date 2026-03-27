using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.Notifications.Application;
using LawWatcher.Notifications.Domain.BillAlerts;

namespace LawWatcher.Notifications.Infrastructure;

public sealed class InMemoryBillAlertRepository : IBillAlertRepository
{
    private readonly Dictionary<string, List<IDomainEvent>> _streams = new(StringComparer.Ordinal);
    private readonly HashSet<string> _profileBillPairs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public Task<bool> ExistsAsync(Guid profileId, Guid billId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_profileBillPairs.Contains(GetPairKey(profileId, billId)));
        }
    }

    public Task SaveAsync(BillAlert alert, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pendingEvents = alert.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return Task.CompletedTask;
        }

        var streamId = GetStreamId(alert.Id);
        var expectedVersion = alert.Version - pendingEvents.Length;
        var pairKey = GetPairKey(alert.ProfileId, alert.BillId);

        lock (_gate)
        {
            if (_profileBillPairs.Contains(pairKey))
            {
                throw new InvalidOperationException($"Alert for pair '{pairKey}' already exists.");
            }

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
            _profileBillPairs.Add(pairKey);
        }

        alert.DequeueUncommittedEvents();
        return Task.CompletedTask;
    }

    private static string GetStreamId(AlertId id) => $"notifications-alert-{id.Value:D}";

    private static string GetPairKey(Guid profileId, Guid billId) => $"{profileId:D}:{billId:D}";
}

public sealed class InMemoryBillAlertProjectionStore : IBillAlertReadRepository, IBillAlertProjection
{
    private readonly Dictionary<Guid, BillAlertReadModel> _alerts = new();
    private readonly Lock _gate = new();

    public Task<IReadOnlyCollection<BillAlertReadModel>> GetAlertsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyCollection<BillAlertReadModel>>(_alerts.Values.ToArray());
        }
    }

    public Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            foreach (var domainEvent in domainEvents)
            {
                if (domainEvent is not BillAlertCreated created)
                {
                    continue;
                }

                _alerts[created.AlertId.Value] = new BillAlertReadModel(
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
        }

        return Task.CompletedTask;
    }
}
