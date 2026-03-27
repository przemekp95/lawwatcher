using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.LegislativeIntake.Application;
using LawWatcher.LegislativeIntake.Domain.Bills;

namespace LawWatcher.LegislativeIntake.Infrastructure;

public sealed class InMemoryImportedBillRepository : IImportedBillRepository
{
    private readonly Dictionary<string, List<IDomainEvent>> _streams = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<ImportedBill?> GetAsync(BillId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_streams.TryGetValue(GetStreamId(id), out var history))
            {
                return Task.FromResult<ImportedBill?>(null);
            }

            return Task.FromResult<ImportedBill?>(ImportedBill.Rehydrate(history.ToArray()));
        }
    }

    public Task SaveAsync(ImportedBill bill, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pendingEvents = bill.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return Task.CompletedTask;
        }

        var streamId = GetStreamId(bill.Id);
        var expectedVersion = bill.Version - pendingEvents.Length;

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

        bill.DequeueUncommittedEvents();
        return Task.CompletedTask;
    }

    private static string GetStreamId(BillId id) => $"legislative-intake-bill-{id.Value:D}";
}

public sealed class InMemoryImportedBillProjectionStore : IImportedBillReadRepository, IImportedBillProjection
{
    private readonly Dictionary<Guid, ProjectionState> _bills = new();
    private readonly Lock _gate = new();

    public Task<IReadOnlyCollection<ImportedBillReadModel>> GetBillsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var snapshot = _bills.Values
                .Select(state => state.ToReadModel())
                .ToArray();

            return Task.FromResult<IReadOnlyCollection<ImportedBillReadModel>>(snapshot);
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
                    case BillImported imported:
                        _bills[imported.BillId.Value] = ProjectionState.From(imported);
                        break;
                    case BillDocumentAttached attached when _bills.TryGetValue(attached.BillId.Value, out var existing):
                        existing.AddDocumentKind(attached.Kind);
                        break;
                }
            }
        }

        return Task.CompletedTask;
    }

    private sealed class ProjectionState
    {
        private readonly HashSet<string> _documentKinds = new(StringComparer.OrdinalIgnoreCase);

        private ProjectionState(Guid id, string sourceSystem, string externalId, string title, string sourceUrl, DateOnly submittedOn)
        {
            Id = id;
            SourceSystem = sourceSystem;
            ExternalId = externalId;
            Title = title;
            SourceUrl = sourceUrl;
            SubmittedOn = submittedOn;
        }

        public Guid Id { get; }

        public string SourceSystem { get; }

        public string ExternalId { get; }

        public string Title { get; }

        public string SourceUrl { get; }

        public DateOnly SubmittedOn { get; }

        public static ProjectionState From(BillImported imported)
        {
            return new ProjectionState(
                imported.BillId.Value,
                imported.SourceSystem,
                imported.ExternalId,
                imported.Title,
                imported.SourceUrl,
                imported.SubmittedOn);
        }

        public void AddDocumentKind(string kind)
        {
            _documentKinds.Add(kind);
        }

        public ImportedBillReadModel ToReadModel()
        {
            return new ImportedBillReadModel(
                Id,
                SourceSystem,
                ExternalId,
                Title,
                SourceUrl,
                SubmittedOn,
                _documentKinds.OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }
}
