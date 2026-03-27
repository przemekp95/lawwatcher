using LawWatcher.AiEnrichment.Application;
using LawWatcher.AiEnrichment.Domain.Tasks;
using LawWatcher.BuildingBlocks.Domain;

namespace LawWatcher.AiEnrichment.Infrastructure;

public sealed class InMemoryAiEnrichmentTaskRepository : IAiEnrichmentTaskRepository
{
    private readonly Dictionary<string, List<IDomainEvent>> _streams = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<AiEnrichmentTask?> GetAsync(AiEnrichmentTaskId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_streams.TryGetValue(GetStreamId(id), out var history))
            {
                return Task.FromResult<AiEnrichmentTask?>(null);
            }

            return Task.FromResult<AiEnrichmentTask?>(AiEnrichmentTask.Rehydrate(history.ToArray()));
        }
    }

    public Task<AiEnrichmentTask?> GetNextQueuedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var next = _streams.Values
                .Select(history => AiEnrichmentTask.Rehydrate(history.ToArray()))
                .Where(task => task.Status.Code == "queued")
                .OrderBy(task => task.RequestedAtUtc)
                .ThenBy(task => task.Subject.Title, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            return Task.FromResult<AiEnrichmentTask?>(next);
        }
    }

    public Task SaveAsync(AiEnrichmentTask task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pendingEvents = task.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return Task.CompletedTask;
        }

        var streamId = GetStreamId(task.Id);
        var expectedVersion = task.Version - pendingEvents.Length;

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

        task.DequeueUncommittedEvents();
        return Task.CompletedTask;
    }

    private static string GetStreamId(AiEnrichmentTaskId id) => $"ai-enrichment-task-{id.Value:D}";
}

public sealed class InMemoryAiEnrichmentTaskProjectionStore : IAiEnrichmentTaskReadRepository, IAiEnrichmentTaskProjection
{
    private readonly Dictionary<Guid, ProjectionState> _tasks = new();
    private readonly Lock _gate = new();

    public Task<IReadOnlyCollection<AiEnrichmentTaskReadModel>> GetTasksAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyCollection<AiEnrichmentTaskReadModel>>(
                _tasks.Values
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
                    case AiEnrichmentRequested requested:
                        _tasks[requested.TaskId.Value] = ProjectionState.From(requested);
                        break;
                    case AiEnrichmentProcessingStarted started when _tasks.TryGetValue(started.TaskId.Value, out var runningTask):
                        runningTask.MarkStarted(started.OccurredAtUtc);
                        break;
                    case AiEnrichmentCompleted completed when _tasks.TryGetValue(completed.TaskId.Value, out var completedTask):
                        completedTask.MarkCompleted(completed.Model, completed.Content, completed.Citations, completed.OccurredAtUtc);
                        break;
                    case AiEnrichmentFailed failed when _tasks.TryGetValue(failed.TaskId.Value, out var failedTask):
                        failedTask.MarkFailed(failed.Error, failed.OccurredAtUtc);
                        break;
                }
            }
        }

        return Task.CompletedTask;
    }

    private sealed class ProjectionState
    {
        private readonly List<string> _citations = [];

        private ProjectionState(
            Guid id,
            string kind,
            string subjectType,
            Guid subjectId,
            string subjectTitle,
            DateTimeOffset requestedAtUtc)
        {
            Id = id;
            Kind = kind;
            SubjectType = subjectType;
            SubjectId = subjectId;
            SubjectTitle = subjectTitle;
            RequestedAtUtc = requestedAtUtc;
            Status = "queued";
        }

        public Guid Id { get; }

        public string Kind { get; }

        public string SubjectType { get; }

        public Guid SubjectId { get; }

        public string SubjectTitle { get; }

        public string Status { get; private set; }

        public string? Model { get; private set; }

        public string? Content { get; private set; }

        public string? Error { get; private set; }

        public DateTimeOffset RequestedAtUtc { get; }

        public DateTimeOffset? StartedAtUtc { get; private set; }

        public DateTimeOffset? CompletedAtUtc { get; private set; }

        public DateTimeOffset? FailedAtUtc { get; private set; }

        public static ProjectionState From(AiEnrichmentRequested requested)
        {
            return new ProjectionState(
                requested.TaskId.Value,
                requested.Kind,
                requested.SubjectType,
                requested.SubjectId,
                requested.SubjectTitle,
                requested.OccurredAtUtc);
        }

        public void MarkStarted(DateTimeOffset occurredAtUtc)
        {
            Status = "running";
            StartedAtUtc = occurredAtUtc;
            FailedAtUtc = null;
        }

        public void MarkCompleted(string model, string content, IReadOnlyCollection<string> citations, DateTimeOffset occurredAtUtc)
        {
            Status = "completed";
            Model = model;
            Content = content;
            Error = null;
            StartedAtUtc ??= occurredAtUtc;
            CompletedAtUtc = occurredAtUtc;
            FailedAtUtc = null;
            _citations.Clear();
            _citations.AddRange(citations);
        }

        public void MarkFailed(string error, DateTimeOffset occurredAtUtc)
        {
            Status = "failed";
            Error = error;
            StartedAtUtc ??= occurredAtUtc;
            CompletedAtUtc = null;
            FailedAtUtc = occurredAtUtc;
        }

        public AiEnrichmentTaskReadModel ToReadModel()
        {
            return new AiEnrichmentTaskReadModel(
                Id,
                Kind,
                SubjectType,
                SubjectId,
                SubjectTitle,
                Status,
                Model,
                Content,
                Error,
                _citations.ToArray(),
                RequestedAtUtc,
                StartedAtUtc,
                CompletedAtUtc,
                FailedAtUtc);
        }
    }
}
