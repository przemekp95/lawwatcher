using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Domain.Backfills;

namespace LawWatcher.IntegrationApi.Infrastructure;

public sealed class InMemoryBackfillRequestRepository : IBackfillRequestRepository
{
    private readonly Dictionary<string, List<IDomainEvent>> _streams = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<BackfillRequest?> GetAsync(BackfillRequestId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_streams.TryGetValue(GetStreamId(id), out var history))
            {
                return Task.FromResult<BackfillRequest?>(null);
            }

            return Task.FromResult<BackfillRequest?>(BackfillRequest.Rehydrate(history.ToArray()));
        }
    }

    public Task<BackfillRequest?> GetNextQueuedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var next = _streams.Values
                .Select(history => BackfillRequest.Rehydrate(history.ToArray()))
                .Where(backfill => backfill.Status.Code == "queued")
                .OrderBy(backfill => backfill.RequestedAtUtc)
                .ThenBy(backfill => backfill.Source.Value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(backfill => backfill.Scope.Value, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            return Task.FromResult<BackfillRequest?>(next);
        }
    }

    public Task SaveAsync(BackfillRequest backfillRequest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pendingEvents = backfillRequest.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return Task.CompletedTask;
        }

        var streamId = GetStreamId(backfillRequest.Id);
        var expectedVersion = backfillRequest.Version - pendingEvents.Length;

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

        backfillRequest.DequeueUncommittedEvents();
        return Task.CompletedTask;
    }

    private static string GetStreamId(BackfillRequestId id) => $"integration-api-backfill-{id.Value:D}";
}

public sealed class InMemoryBackfillRequestProjectionStore : IBackfillRequestReadRepository, IBackfillRequestProjection
{
    private readonly Dictionary<Guid, ProjectionState> _backfills = new();
    private readonly Lock _gate = new();

    public Task<IReadOnlyCollection<BackfillRequestReadModel>> GetBackfillsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyCollection<BackfillRequestReadModel>>(
                _backfills.Values
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
                    case BackfillRequested requested:
                        _backfills[requested.BackfillRequestId.Value] = ProjectionState.From(requested);
                        break;
                    case BackfillStarted started when _backfills.TryGetValue(started.BackfillRequestId.Value, out var startedBackfill):
                        startedBackfill.MarkStarted(started.OccurredAtUtc);
                        break;
                    case BackfillCompleted completed when _backfills.TryGetValue(completed.BackfillRequestId.Value, out var completedBackfill):
                        completedBackfill.MarkCompleted(completed.OccurredAtUtc);
                        break;
                }
            }
        }

        return Task.CompletedTask;
    }

    private sealed class ProjectionState
    {
        private ProjectionState(
            Guid id,
            string source,
            string scope,
            string requestedBy,
            DateOnly requestedFrom,
            DateOnly? requestedTo,
            DateTimeOffset requestedAtUtc)
        {
            Id = id;
            Source = source;
            Scope = scope;
            RequestedBy = requestedBy;
            RequestedFrom = requestedFrom;
            RequestedTo = requestedTo;
            RequestedAtUtc = requestedAtUtc;
            Status = "queued";
        }

        public Guid Id { get; }

        public string Source { get; }

        public string Scope { get; }

        public string Status { get; private set; }

        public string RequestedBy { get; }

        public DateOnly RequestedFrom { get; }

        public DateOnly? RequestedTo { get; }

        public DateTimeOffset RequestedAtUtc { get; }

        public DateTimeOffset? StartedAtUtc { get; private set; }

        public DateTimeOffset? CompletedAtUtc { get; private set; }

        public static ProjectionState From(BackfillRequested requested)
        {
            return new ProjectionState(
                requested.BackfillRequestId.Value,
                requested.Source,
                requested.Scope,
                requested.RequestedBy,
                requested.RequestedFrom,
                requested.RequestedTo,
                requested.OccurredAtUtc);
        }

        public void MarkStarted(DateTimeOffset occurredAtUtc)
        {
            Status = "running";
            StartedAtUtc = occurredAtUtc;
        }

        public void MarkCompleted(DateTimeOffset occurredAtUtc)
        {
            Status = "completed";
            CompletedAtUtc = occurredAtUtc;
            StartedAtUtc ??= occurredAtUtc;
        }

        public BackfillRequestReadModel ToReadModel()
        {
            return new BackfillRequestReadModel(
                Id,
                Source,
                Scope,
                Status,
                RequestedBy,
                RequestedFrom,
                RequestedTo,
                RequestedAtUtc,
                StartedAtUtc,
                CompletedAtUtc);
        }
    }
}
