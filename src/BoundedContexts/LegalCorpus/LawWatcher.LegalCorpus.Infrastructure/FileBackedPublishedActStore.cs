using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegalCorpus.Domain.Acts;

namespace LawWatcher.LegalCorpus.Infrastructure;

public sealed class FileBackedPublishedActRepository(string rootPath) : IPublishedActRepository
{
    private readonly string _rootPath = rootPath;
    private readonly string _streamsDirectory = Path.Combine(rootPath, "streams");

    public Task<PublishedAct?> GetAsync(ActId id, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    GetStreamPath(id),
                    () => new PublishedActStreamDocument([]),
                    ct);

                return document.Events.Length == 0
                    ? null
                    : PublishedAct.Rehydrate(document.Events.Select(ToDomainEvent).ToArray());
            },
            cancellationToken);
    }

    public Task SaveAsync(PublishedAct act, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var pendingEvents = act.UncommittedEvents.ToArray();
                if (pendingEvents.Length == 0)
                {
                    return;
                }

                var streamPath = GetStreamPath(act.Id);
                var document = await JsonFilePersistence.LoadAsync(
                    streamPath,
                    () => new PublishedActStreamDocument([]),
                    ct);

                var expectedVersion = act.Version - pendingEvents.Length;
                if (document.Events.Length != expectedVersion)
                {
                    throw new InvalidOperationException($"Optimistic concurrency violation for published act stream '{act.Id.Value:D}'.");
                }

                await JsonFilePersistence.SaveAsync(
                    streamPath,
                    new PublishedActStreamDocument(document.Events.Concat(pendingEvents.Select(FromDomainEvent)).ToArray()),
                    ct);

                act.DequeueUncommittedEvents();
            },
            cancellationToken);
    }

    private string GetStreamPath(ActId id) => Path.Combine(_streamsDirectory, $"{id.Value:D}.json");

    private static PublishedActEventRecord FromDomainEvent(IDomainEvent domainEvent) =>
        domainEvent switch
        {
            PublishedActRegistered registered => new PublishedActEventRecord(
                "registered",
                registered.ActId.Value,
                registered.BillId,
                registered.BillTitle,
                registered.BillExternalId,
                registered.Eli,
                registered.Title,
                registered.PublishedOn,
                registered.EffectiveFrom,
                null,
                null,
                registered.OccurredAtUtc),
            ActArtifactAttached attached => new PublishedActEventRecord(
                "artifact-attached",
                attached.ActId.Value,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                attached.Kind,
                attached.ObjectKey,
                attached.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported published act domain event type '{domainEvent.GetType().Name}'.")
        };

    private static IDomainEvent ToDomainEvent(PublishedActEventRecord record) =>
        record.Type switch
        {
            "registered" => new PublishedActRegistered(
                new ActId(record.ActId),
                record.BillId ?? throw new InvalidOperationException("Published act registered event is missing bill id."),
                record.BillTitle ?? throw new InvalidOperationException("Published act registered event is missing bill title."),
                record.BillExternalId ?? throw new InvalidOperationException("Published act registered event is missing bill external id."),
                record.Eli ?? throw new InvalidOperationException("Published act registered event is missing ELI."),
                record.Title ?? throw new InvalidOperationException("Published act registered event is missing title."),
                record.PublishedOn ?? throw new InvalidOperationException("Published act registered event is missing published date."),
                record.EffectiveFrom,
                record.OccurredAtUtc),
            "artifact-attached" => new ActArtifactAttached(
                new ActId(record.ActId),
                record.ArtifactKind ?? throw new InvalidOperationException("Published act artifact event is missing kind."),
                record.ArtifactObjectKey ?? throw new InvalidOperationException("Published act artifact event is missing object key."),
                record.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported published act event record type '{record.Type}'.")
        };

    private sealed record PublishedActStreamDocument(PublishedActEventRecord[] Events);

    private sealed record PublishedActEventRecord(
        string Type,
        Guid ActId,
        Guid? BillId,
        string? BillTitle,
        string? BillExternalId,
        string? Eli,
        string? Title,
        DateOnly? PublishedOn,
        DateOnly? EffectiveFrom,
        string? ArtifactKind,
        string? ArtifactObjectKey,
        DateTimeOffset OccurredAtUtc);
}

public sealed class FileBackedPublishedActProjectionStore(string rootPath) : IPublishedActReadRepository, IPublishedActProjection
{
    private readonly string _rootPath = rootPath;
    private readonly string _projectionPath = Path.Combine(rootPath, "projection.json");

    public Task<IReadOnlyCollection<PublishedActReadModel>> GetActsAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new PublishedActProjectionDocument([]),
                    ct);

                return (IReadOnlyCollection<PublishedActReadModel>)document.Acts
                    .Select(record => new PublishedActReadModel(
                        record.Id,
                        record.BillId,
                        record.BillTitle,
                        record.BillExternalId,
                        record.Eli,
                        record.Title,
                        record.PublishedOn,
                        record.EffectiveFrom,
                        record.ArtifactKinds))
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
                    () => new PublishedActProjectionDocument([]),
                    ct);

                var acts = document.Acts.ToDictionary(act => act.Id);
                foreach (var domainEvent in domainEvents)
                {
                    switch (domainEvent)
                    {
                        case PublishedActRegistered registered:
                            acts[registered.ActId.Value] = new PublishedActProjectionRecord(
                                registered.ActId.Value,
                                registered.BillId,
                                registered.BillTitle,
                                registered.BillExternalId,
                                registered.Eli,
                                registered.Title,
                                registered.PublishedOn,
                                registered.EffectiveFrom,
                                []);
                            break;
                        case ActArtifactAttached attached when acts.TryGetValue(attached.ActId.Value, out var existing):
                            acts[attached.ActId.Value] = existing with
                            {
                                ArtifactKinds = existing.ArtifactKinds
                                    .Append(attached.Kind)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
                                    .ToArray()
                            };
                            break;
                    }
                }

                await JsonFilePersistence.SaveAsync(
                    _projectionPath,
                    new PublishedActProjectionDocument(
                        acts.Values
                            .OrderByDescending(act => act.PublishedOn)
                            .ThenBy(act => act.Title, StringComparer.OrdinalIgnoreCase)
                            .ToArray()),
                    ct);
            },
            cancellationToken);
    }

    private sealed record PublishedActProjectionDocument(PublishedActProjectionRecord[] Acts);

    private sealed record PublishedActProjectionRecord(
        Guid Id,
        Guid BillId,
        string BillTitle,
        string BillExternalId,
        string Eli,
        string Title,
        DateOnly PublishedOn,
        DateOnly? EffectiveFrom,
        string[] ArtifactKinds);
}
