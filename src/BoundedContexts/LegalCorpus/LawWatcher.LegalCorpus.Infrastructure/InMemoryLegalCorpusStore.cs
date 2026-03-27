using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegalCorpus.Domain.Acts;

namespace LawWatcher.LegalCorpus.Infrastructure;

public sealed class InMemoryPublishedActRepository : IPublishedActRepository
{
    private readonly Dictionary<string, List<IDomainEvent>> _streams = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<PublishedAct?> GetAsync(ActId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_streams.TryGetValue(GetStreamId(id), out var history))
            {
                return Task.FromResult<PublishedAct?>(null);
            }

            return Task.FromResult<PublishedAct?>(PublishedAct.Rehydrate(history.ToArray()));
        }
    }

    public Task SaveAsync(PublishedAct act, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pendingEvents = act.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return Task.CompletedTask;
        }

        var streamId = GetStreamId(act.Id);
        var expectedVersion = act.Version - pendingEvents.Length;

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

        act.DequeueUncommittedEvents();
        return Task.CompletedTask;
    }

    private static string GetStreamId(ActId id) => $"legal-corpus-act-{id.Value:D}";
}

public sealed class InMemoryPublishedActProjectionStore : IPublishedActReadRepository, IPublishedActProjection
{
    private readonly Dictionary<Guid, ProjectionState> _acts = new();
    private readonly Lock _gate = new();

    public Task<IReadOnlyCollection<PublishedActReadModel>> GetActsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyCollection<PublishedActReadModel>>(
                _acts.Values
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
                    case PublishedActRegistered registered:
                        _acts[registered.ActId.Value] = ProjectionState.From(registered);
                        break;
                    case ActArtifactAttached attached when _acts.TryGetValue(attached.ActId.Value, out var existing):
                        existing.AddArtifactKind(attached.Kind);
                        break;
                }
            }
        }

        return Task.CompletedTask;
    }

    private sealed class ProjectionState
    {
        private readonly HashSet<string> _artifactKinds = new(StringComparer.OrdinalIgnoreCase);

        private ProjectionState(
            Guid id,
            Guid billId,
            string billTitle,
            string billExternalId,
            string eli,
            string title,
            DateOnly publishedOn,
            DateOnly? effectiveFrom)
        {
            Id = id;
            BillId = billId;
            BillTitle = billTitle;
            BillExternalId = billExternalId;
            Eli = eli;
            Title = title;
            PublishedOn = publishedOn;
            EffectiveFrom = effectiveFrom;
        }

        public Guid Id { get; }

        public Guid BillId { get; }

        public string BillTitle { get; }

        public string BillExternalId { get; }

        public string Eli { get; }

        public string Title { get; }

        public DateOnly PublishedOn { get; }

        public DateOnly? EffectiveFrom { get; }

        public static ProjectionState From(PublishedActRegistered registered)
        {
            return new ProjectionState(
                registered.ActId.Value,
                registered.BillId,
                registered.BillTitle,
                registered.BillExternalId,
                registered.Eli,
                registered.Title,
                registered.PublishedOn,
                registered.EffectiveFrom);
        }

        public void AddArtifactKind(string kind)
        {
            _artifactKinds.Add(kind);
        }

        public PublishedActReadModel ToReadModel()
        {
            return new PublishedActReadModel(
                Id,
                BillId,
                BillTitle,
                BillExternalId,
                Eli,
                Title,
                PublishedOn,
                EffectiveFrom,
                _artifactKinds.OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }
}
