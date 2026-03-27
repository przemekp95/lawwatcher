using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Domain.Backfills;

namespace LawWatcher.IntegrationApi.Infrastructure;

public sealed class FileBackedBackfillRequestRepository(string rootPath) : IBackfillRequestRepository
{
    private readonly string _rootPath = rootPath;
    private readonly string _streamsDirectory = Path.Combine(rootPath, "streams");

    public Task<BackfillRequest?> GetAsync(BackfillRequestId id, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    GetStreamPath(id),
                    () => new BackfillRequestStreamDocument([]),
                    ct);

                return document.Events.Length == 0
                    ? null
                    : BackfillRequest.Rehydrate(document.Events.Select(ToDomainEvent).ToArray());
            },
            cancellationToken);
    }

    public Task<BackfillRequest?> GetNextQueuedAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                if (!Directory.Exists(_streamsDirectory))
                {
                    return null;
                }

                var queuedRequests = new List<BackfillRequest>();
                foreach (var streamPath in Directory.EnumerateFiles(_streamsDirectory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    var document = await JsonFilePersistence.LoadAsync(
                        streamPath,
                        () => new BackfillRequestStreamDocument([]),
                        ct);

                    if (document.Events.Length == 0)
                    {
                        continue;
                    }

                    var backfillRequest = BackfillRequest.Rehydrate(document.Events.Select(ToDomainEvent).ToArray());
                    if (backfillRequest.Status.Code == "queued")
                    {
                        queuedRequests.Add(backfillRequest);
                    }
                }

                return queuedRequests
                    .OrderBy(backfill => backfill.RequestedAtUtc)
                    .ThenBy(backfill => backfill.Source.Value, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(backfill => backfill.Scope.Value, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            },
            cancellationToken);
    }

    public Task SaveAsync(BackfillRequest backfillRequest, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var pendingEvents = backfillRequest.UncommittedEvents.ToArray();
                if (pendingEvents.Length == 0)
                {
                    return;
                }

                var streamPath = GetStreamPath(backfillRequest.Id);
                var document = await JsonFilePersistence.LoadAsync(
                    streamPath,
                    () => new BackfillRequestStreamDocument([]),
                    ct);

                var expectedVersion = backfillRequest.Version - pendingEvents.Length;
                if (document.Events.Length != expectedVersion)
                {
                    throw new InvalidOperationException($"Optimistic concurrency violation for backfill stream '{backfillRequest.Id.Value:D}'.");
                }

                var updatedEvents = document.Events
                    .Concat(pendingEvents.Select(FromDomainEvent))
                    .ToArray();

                await JsonFilePersistence.SaveAsync(
                    streamPath,
                    new BackfillRequestStreamDocument(updatedEvents),
                    ct);

                backfillRequest.DequeueUncommittedEvents();
            },
            cancellationToken);
    }

    private string GetStreamPath(BackfillRequestId id) => Path.Combine(_streamsDirectory, $"{id.Value:D}.json");

    private static BackfillEventRecord FromDomainEvent(IDomainEvent domainEvent) =>
        domainEvent switch
        {
            BackfillRequested requested => new BackfillEventRecord(
                "requested",
                requested.BackfillRequestId.Value,
                requested.Source,
                requested.Scope,
                requested.RequestedFrom,
                requested.RequestedTo,
                requested.RequestedBy,
                requested.OccurredAtUtc),
            BackfillStarted started => new BackfillEventRecord(
                "started",
                started.BackfillRequestId.Value,
                null,
                null,
                null,
                null,
                null,
                started.OccurredAtUtc),
            BackfillCompleted completed => new BackfillEventRecord(
                "completed",
                completed.BackfillRequestId.Value,
                null,
                null,
                null,
                null,
                null,
                completed.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported backfill domain event type '{domainEvent.GetType().Name}'.")
        };

    private static IDomainEvent ToDomainEvent(BackfillEventRecord record) =>
        record.Type switch
        {
            "requested" => new BackfillRequested(
                new BackfillRequestId(record.BackfillRequestId),
                record.Source ?? throw new InvalidOperationException("Backfill requested event is missing source."),
                record.Scope ?? throw new InvalidOperationException("Backfill requested event is missing scope."),
                record.RequestedFrom ?? throw new InvalidOperationException("Backfill requested event is missing requested-from date."),
                record.RequestedTo,
                record.RequestedBy ?? throw new InvalidOperationException("Backfill requested event is missing requester."),
                record.OccurredAtUtc),
            "started" => new BackfillStarted(new BackfillRequestId(record.BackfillRequestId), record.OccurredAtUtc),
            "completed" => new BackfillCompleted(new BackfillRequestId(record.BackfillRequestId), record.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported backfill event record type '{record.Type}'.")
        };

    private sealed record BackfillRequestStreamDocument(BackfillEventRecord[] Events);

    private sealed record BackfillEventRecord(
        string Type,
        Guid BackfillRequestId,
        string? Source,
        string? Scope,
        DateOnly? RequestedFrom,
        DateOnly? RequestedTo,
        string? RequestedBy,
        DateTimeOffset OccurredAtUtc);
}

public sealed class FileBackedBackfillRequestProjectionStore(string rootPath) : IBackfillRequestReadRepository, IBackfillRequestProjection
{
    private readonly string _rootPath = rootPath;
    private readonly string _projectionPath = Path.Combine(rootPath, "projection.json");

    public Task<IReadOnlyCollection<BackfillRequestReadModel>> GetBackfillsAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new BackfillRequestProjectionDocument([]),
                    ct);

                return (IReadOnlyCollection<BackfillRequestReadModel>)document.Backfills.ToArray();
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
                    () => new BackfillRequestProjectionDocument([]),
                    ct);

                var backfills = document.Backfills.ToDictionary(backfill => backfill.Id);
                foreach (var domainEvent in domainEvents)
                {
                    switch (domainEvent)
                    {
                        case BackfillRequested requested:
                            backfills[requested.BackfillRequestId.Value] = new BackfillRequestReadModel(
                                requested.BackfillRequestId.Value,
                                requested.Source,
                                requested.Scope,
                                "queued",
                                requested.RequestedBy,
                                requested.RequestedFrom,
                                requested.RequestedTo,
                                requested.OccurredAtUtc,
                                null,
                                null);
                            break;
                        case BackfillStarted started when backfills.TryGetValue(started.BackfillRequestId.Value, out var runningBackfill):
                            backfills[started.BackfillRequestId.Value] = runningBackfill with
                            {
                                Status = "running",
                                StartedAtUtc = started.OccurredAtUtc
                            };
                            break;
                        case BackfillCompleted completed when backfills.TryGetValue(completed.BackfillRequestId.Value, out var completedBackfill):
                            backfills[completed.BackfillRequestId.Value] = completedBackfill with
                            {
                                Status = "completed",
                                StartedAtUtc = completedBackfill.StartedAtUtc ?? completed.OccurredAtUtc,
                                CompletedAtUtc = completed.OccurredAtUtc
                            };
                            break;
                    }
                }

                await JsonFilePersistence.SaveAsync(
                    _projectionPath,
                    new BackfillRequestProjectionDocument(backfills.Values.ToArray()),
                    ct);
            },
            cancellationToken);
    }

    private sealed record BackfillRequestProjectionDocument(BackfillRequestReadModel[] Backfills);
}
