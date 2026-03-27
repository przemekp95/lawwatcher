using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Domain.Replays;

namespace LawWatcher.IntegrationApi.Infrastructure;

public sealed class FileBackedReplayRequestRepository(string rootPath) : IReplayRequestRepository
{
    private readonly string _rootPath = rootPath;
    private readonly string _streamsDirectory = Path.Combine(rootPath, "streams");

    public Task<ReplayRequest?> GetAsync(ReplayRequestId id, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    GetStreamPath(id),
                    () => new ReplayRequestStreamDocument([]),
                    ct);

                return document.Events.Length == 0
                    ? null
                    : ReplayRequest.Rehydrate(document.Events.Select(ToDomainEvent).ToArray());
            },
            cancellationToken);
    }

    public Task<ReplayRequest?> GetNextQueuedAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                if (!Directory.Exists(_streamsDirectory))
                {
                    return null;
                }

                var queuedRequests = new List<ReplayRequest>();
                foreach (var streamPath in Directory.EnumerateFiles(_streamsDirectory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    var document = await JsonFilePersistence.LoadAsync(
                        streamPath,
                        () => new ReplayRequestStreamDocument([]),
                        ct);

                    if (document.Events.Length == 0)
                    {
                        continue;
                    }

                    var replayRequest = ReplayRequest.Rehydrate(document.Events.Select(ToDomainEvent).ToArray());
                    if (replayRequest.Status.Code == "queued")
                    {
                        queuedRequests.Add(replayRequest);
                    }
                }

                return queuedRequests
                    .OrderBy(replay => replay.RequestedAtUtc)
                    .ThenBy(replay => replay.Scope.Value, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            },
            cancellationToken);
    }

    public Task SaveAsync(ReplayRequest replayRequest, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var pendingEvents = replayRequest.UncommittedEvents.ToArray();
                if (pendingEvents.Length == 0)
                {
                    return;
                }

                var streamPath = GetStreamPath(replayRequest.Id);
                var document = await JsonFilePersistence.LoadAsync(
                    streamPath,
                    () => new ReplayRequestStreamDocument([]),
                    ct);

                var expectedVersion = replayRequest.Version - pendingEvents.Length;
                if (document.Events.Length != expectedVersion)
                {
                    throw new InvalidOperationException($"Optimistic concurrency violation for replay stream '{replayRequest.Id.Value:D}'.");
                }

                var updatedEvents = document.Events
                    .Concat(pendingEvents.Select(FromDomainEvent))
                    .ToArray();

                await JsonFilePersistence.SaveAsync(
                    streamPath,
                    new ReplayRequestStreamDocument(updatedEvents),
                    ct);

                replayRequest.DequeueUncommittedEvents();
            },
            cancellationToken);
    }

    private string GetStreamPath(ReplayRequestId id) => Path.Combine(_streamsDirectory, $"{id.Value:D}.json");

    private static ReplayEventRecord FromDomainEvent(IDomainEvent domainEvent) =>
        domainEvent switch
        {
            ReplayRequested requested => new ReplayEventRecord("requested", requested.ReplayRequestId.Value, requested.Scope, requested.RequestedBy, requested.OccurredAtUtc),
            ReplayStarted started => new ReplayEventRecord("started", started.ReplayRequestId.Value, null, null, started.OccurredAtUtc),
            ReplayCompleted completed => new ReplayEventRecord("completed", completed.ReplayRequestId.Value, null, null, completed.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported replay domain event type '{domainEvent.GetType().Name}'.")
        };

    private static IDomainEvent ToDomainEvent(ReplayEventRecord record) =>
        record.Type switch
        {
            "requested" => new ReplayRequested(
                new ReplayRequestId(record.ReplayRequestId),
                record.Scope ?? throw new InvalidOperationException("Replay requested event is missing scope."),
                record.RequestedBy ?? throw new InvalidOperationException("Replay requested event is missing requester."),
                record.OccurredAtUtc),
            "started" => new ReplayStarted(new ReplayRequestId(record.ReplayRequestId), record.OccurredAtUtc),
            "completed" => new ReplayCompleted(new ReplayRequestId(record.ReplayRequestId), record.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported replay event record type '{record.Type}'.")
        };

    private sealed record ReplayRequestStreamDocument(ReplayEventRecord[] Events);

    private sealed record ReplayEventRecord(
        string Type,
        Guid ReplayRequestId,
        string? Scope,
        string? RequestedBy,
        DateTimeOffset OccurredAtUtc);
}

public sealed class FileBackedReplayRequestProjectionStore(string rootPath) : IReplayRequestReadRepository, IReplayRequestProjection
{
    private readonly string _rootPath = rootPath;
    private readonly string _projectionPath = Path.Combine(rootPath, "projection.json");

    public Task<IReadOnlyCollection<ReplayRequestReadModel>> GetReplaysAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new ReplayRequestProjectionDocument([]),
                    ct);

                return (IReadOnlyCollection<ReplayRequestReadModel>)document.Replays.ToArray();
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
                    () => new ReplayRequestProjectionDocument([]),
                    ct);

                var replays = document.Replays.ToDictionary(replay => replay.Id);
                foreach (var domainEvent in domainEvents)
                {
                    switch (domainEvent)
                    {
                        case ReplayRequested requested:
                            replays[requested.ReplayRequestId.Value] = new ReplayRequestReadModel(
                                requested.ReplayRequestId.Value,
                                requested.Scope,
                                "queued",
                                requested.RequestedBy,
                                requested.OccurredAtUtc,
                                null,
                                null);
                            break;
                        case ReplayStarted started when replays.TryGetValue(started.ReplayRequestId.Value, out var runningReplay):
                            replays[started.ReplayRequestId.Value] = runningReplay with
                            {
                                Status = "running",
                                StartedAtUtc = started.OccurredAtUtc
                            };
                            break;
                        case ReplayCompleted completed when replays.TryGetValue(completed.ReplayRequestId.Value, out var completedReplay):
                            replays[completed.ReplayRequestId.Value] = completedReplay with
                            {
                                Status = "completed",
                                StartedAtUtc = completedReplay.StartedAtUtc ?? completed.OccurredAtUtc,
                                CompletedAtUtc = completed.OccurredAtUtc
                            };
                            break;
                    }
                }

                await JsonFilePersistence.SaveAsync(
                    _projectionPath,
                    new ReplayRequestProjectionDocument(replays.Values.ToArray()),
                    ct);
            },
            cancellationToken);
    }

    private sealed record ReplayRequestProjectionDocument(ReplayRequestReadModel[] Replays);
}
