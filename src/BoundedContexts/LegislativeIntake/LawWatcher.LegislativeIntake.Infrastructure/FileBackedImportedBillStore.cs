using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.LegislativeIntake.Application;
using LawWatcher.LegislativeIntake.Domain.Bills;

namespace LawWatcher.LegislativeIntake.Infrastructure;

public sealed class FileBackedImportedBillRepository(string rootPath) : IImportedBillRepository
{
    private readonly string _rootPath = rootPath;
    private readonly string _streamsDirectory = Path.Combine(rootPath, "streams");

    public Task<ImportedBill?> GetAsync(BillId id, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    GetStreamPath(id),
                    () => new ImportedBillStreamDocument([]),
                    ct);

                return document.Events.Length == 0
                    ? null
                    : ImportedBill.Rehydrate(document.Events.Select(ToDomainEvent).ToArray());
            },
            cancellationToken);
    }

    public Task SaveAsync(ImportedBill bill, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var pendingEvents = bill.UncommittedEvents.ToArray();
                if (pendingEvents.Length == 0)
                {
                    return;
                }

                var streamPath = GetStreamPath(bill.Id);
                var document = await JsonFilePersistence.LoadAsync(
                    streamPath,
                    () => new ImportedBillStreamDocument([]),
                    ct);

                var expectedVersion = bill.Version - pendingEvents.Length;
                if (document.Events.Length != expectedVersion)
                {
                    throw new InvalidOperationException($"Optimistic concurrency violation for imported bill stream '{bill.Id.Value:D}'.");
                }

                await JsonFilePersistence.SaveAsync(
                    streamPath,
                    new ImportedBillStreamDocument(document.Events.Concat(pendingEvents.Select(FromDomainEvent)).ToArray()),
                    ct);

                bill.DequeueUncommittedEvents();
            },
            cancellationToken);
    }

    private string GetStreamPath(BillId id) => Path.Combine(_streamsDirectory, $"{id.Value:D}.json");

    private static ImportedBillEventRecord FromDomainEvent(IDomainEvent domainEvent) =>
        domainEvent switch
        {
            BillImported imported => new ImportedBillEventRecord(
                "imported",
                imported.BillId.Value,
                imported.SourceSystem,
                imported.ExternalId,
                imported.SourceUrl,
                imported.Title,
                imported.SubmittedOn,
                null,
                null,
                imported.OccurredAtUtc),
            BillDocumentAttached attached => new ImportedBillEventRecord(
                "document-attached",
                attached.BillId.Value,
                null,
                null,
                null,
                null,
                null,
                attached.Kind,
                attached.ObjectKey,
                attached.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported imported bill domain event type '{domainEvent.GetType().Name}'.")
        };

    private static IDomainEvent ToDomainEvent(ImportedBillEventRecord record) =>
        record.Type switch
        {
            "imported" => new BillImported(
                new BillId(record.BillId),
                record.SourceSystem ?? throw new InvalidOperationException("Imported bill event is missing source system."),
                record.ExternalId ?? throw new InvalidOperationException("Imported bill event is missing external identifier."),
                record.SourceUrl ?? throw new InvalidOperationException("Imported bill event is missing source URL."),
                record.Title ?? throw new InvalidOperationException("Imported bill event is missing title."),
                record.SubmittedOn ?? throw new InvalidOperationException("Imported bill event is missing submitted date."),
                record.OccurredAtUtc),
            "document-attached" => new BillDocumentAttached(
                new BillId(record.BillId),
                record.DocumentKind ?? throw new InvalidOperationException("Bill document event is missing document kind."),
                record.DocumentObjectKey ?? throw new InvalidOperationException("Bill document event is missing object key."),
                record.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported imported bill event record type '{record.Type}'.")
        };

    private sealed record ImportedBillStreamDocument(ImportedBillEventRecord[] Events);

    private sealed record ImportedBillEventRecord(
        string Type,
        Guid BillId,
        string? SourceSystem,
        string? ExternalId,
        string? SourceUrl,
        string? Title,
        DateOnly? SubmittedOn,
        string? DocumentKind,
        string? DocumentObjectKey,
        DateTimeOffset OccurredAtUtc);
}

public sealed class FileBackedImportedBillProjectionStore(string rootPath) : IImportedBillReadRepository, IImportedBillProjection
{
    private readonly string _rootPath = rootPath;
    private readonly string _projectionPath = Path.Combine(rootPath, "projection.json");

    public Task<IReadOnlyCollection<ImportedBillReadModel>> GetBillsAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new ImportedBillProjectionDocument([]),
                    ct);

                return (IReadOnlyCollection<ImportedBillReadModel>)document.Bills
                    .Select(record => new ImportedBillReadModel(
                        record.Id,
                        record.SourceSystem,
                        record.ExternalId,
                        record.Title,
                        record.SourceUrl,
                        record.SubmittedOn,
                        record.DocumentKinds))
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
                    () => new ImportedBillProjectionDocument([]),
                    ct);

                var bills = document.Bills.ToDictionary(record => record.Id);
                foreach (var domainEvent in domainEvents)
                {
                    switch (domainEvent)
                    {
                        case BillImported imported:
                            bills[imported.BillId.Value] = new ImportedBillProjectionRecord(
                                imported.BillId.Value,
                                imported.SourceSystem,
                                imported.ExternalId,
                                imported.Title,
                                imported.SourceUrl,
                                imported.SubmittedOn,
                                []);
                            break;
                        case BillDocumentAttached attached when bills.TryGetValue(attached.BillId.Value, out var bill):
                            bills[attached.BillId.Value] = bill with
                            {
                                DocumentKinds = bill.DocumentKinds
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
                    new ImportedBillProjectionDocument(
                        bills.Values
                            .OrderByDescending(bill => bill.SubmittedOn)
                            .ThenBy(bill => bill.Title, StringComparer.OrdinalIgnoreCase)
                            .ToArray()),
                    ct);
            },
            cancellationToken);
    }

    private sealed record ImportedBillProjectionDocument(ImportedBillProjectionRecord[] Bills);

    private sealed record ImportedBillProjectionRecord(
        Guid Id,
        string SourceSystem,
        string ExternalId,
        string Title,
        string SourceUrl,
        DateOnly SubmittedOn,
        string[] DocumentKinds);
}
