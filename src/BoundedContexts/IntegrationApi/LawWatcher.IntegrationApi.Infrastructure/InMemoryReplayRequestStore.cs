using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Domain.Replays;

namespace LawWatcher.IntegrationApi.Infrastructure;

public sealed class InMemoryReplayRequestRepository : IReplayRequestRepository
{
    private readonly Dictionary<string, List<IDomainEvent>> _streams = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<ReplayRequest?> GetAsync(ReplayRequestId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_streams.TryGetValue(GetStreamId(id), out var history))
            {
                return Task.FromResult<ReplayRequest?>(null);
            }

            return Task.FromResult<ReplayRequest?>(ReplayRequest.Rehydrate(history.ToArray()));
        }
    }

    public Task<ReplayRequest?> GetNextQueuedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var next = _streams.Values
                .Select(history => ReplayRequest.Rehydrate(history.ToArray()))
                .Where(replay => replay.Status.Code == "queued")
                .OrderBy(replay => replay.RequestedAtUtc)
                .ThenBy(replay => replay.Scope.Value, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            return Task.FromResult<ReplayRequest?>(next);
        }
    }

    public Task SaveAsync(ReplayRequest replayRequest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pendingEvents = replayRequest.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return Task.CompletedTask;
        }

        var streamId = GetStreamId(replayRequest.Id);
        var expectedVersion = replayRequest.Version - pendingEvents.Length;

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

        replayRequest.DequeueUncommittedEvents();
        return Task.CompletedTask;
    }

    private static string GetStreamId(ReplayRequestId id) => $"integration-api-replay-{id.Value:D}";
}

public sealed class InMemoryReplayRequestProjectionStore : IReplayRequestReadRepository, IReplayRequestProjection
{
    private readonly Dictionary<Guid, ProjectionState> _replays = new();
    private readonly Lock _gate = new();

    public Task<IReadOnlyCollection<ReplayRequestReadModel>> GetReplaysAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyCollection<ReplayRequestReadModel>>(
                _replays.Values
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
                    case ReplayRequested requested:
                        _replays[requested.ReplayRequestId.Value] = ProjectionState.From(requested);
                        break;
                    case ReplayStarted started when _replays.TryGetValue(started.ReplayRequestId.Value, out var startedReplay):
                        startedReplay.MarkStarted(started.OccurredAtUtc);
                        break;
                    case ReplayCompleted completed when _replays.TryGetValue(completed.ReplayRequestId.Value, out var completedReplay):
                        completedReplay.MarkCompleted(completed.OccurredAtUtc);
                        break;
                }
            }
        }

        return Task.CompletedTask;
    }

    private sealed class ProjectionState
    {
        private ProjectionState(Guid id, string scope, string requestedBy, DateTimeOffset requestedAtUtc)
        {
            Id = id;
            Scope = scope;
            RequestedBy = requestedBy;
            RequestedAtUtc = requestedAtUtc;
            Status = "queued";
        }

        public Guid Id { get; }

        public string Scope { get; }

        public string Status { get; private set; }

        public string RequestedBy { get; }

        public DateTimeOffset RequestedAtUtc { get; }

        public DateTimeOffset? StartedAtUtc { get; private set; }

        public DateTimeOffset? CompletedAtUtc { get; private set; }

        public static ProjectionState From(ReplayRequested requested)
        {
            return new ProjectionState(
                requested.ReplayRequestId.Value,
                requested.Scope,
                requested.RequestedBy,
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

        public ReplayRequestReadModel ToReadModel()
        {
            return new ReplayRequestReadModel(
                Id,
                Scope,
                Status,
                RequestedBy,
                RequestedAtUtc,
                StartedAtUtc,
                CompletedAtUtc);
        }
    }
}
