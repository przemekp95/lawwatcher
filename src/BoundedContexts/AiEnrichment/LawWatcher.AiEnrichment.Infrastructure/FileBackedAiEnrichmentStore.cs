using LawWatcher.AiEnrichment.Application;
using LawWatcher.AiEnrichment.Domain.Tasks;
using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Persistence;

namespace LawWatcher.AiEnrichment.Infrastructure;

public sealed class FileBackedAiEnrichmentTaskRepository(string rootPath) : IAiEnrichmentTaskRepository
{
    private readonly string _rootPath = rootPath;
    private readonly string _streamsDirectory = Path.Combine(rootPath, "streams");

    public Task<AiEnrichmentTask?> GetAsync(AiEnrichmentTaskId id, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    GetStreamPath(id),
                    () => new AiEnrichmentTaskStreamDocument([]),
                    ct);

                return document.Events.Length == 0
                    ? null
                    : AiEnrichmentTask.Rehydrate(document.Events.Select(ToDomainEvent).ToArray());
            },
            cancellationToken);
    }

    public Task<AiEnrichmentTask?> GetNextQueuedAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                if (!Directory.Exists(_streamsDirectory))
                {
                    return null;
                }

                var queuedTasks = new List<AiEnrichmentTask>();
                foreach (var streamPath in Directory.EnumerateFiles(_streamsDirectory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    var document = await JsonFilePersistence.LoadAsync(
                        streamPath,
                        () => new AiEnrichmentTaskStreamDocument([]),
                        ct);

                    if (document.Events.Length == 0)
                    {
                        continue;
                    }

                    var task = AiEnrichmentTask.Rehydrate(document.Events.Select(ToDomainEvent).ToArray());
                    if (task.Status.Code == "queued")
                    {
                        queuedTasks.Add(task);
                    }
                }

                return queuedTasks
                    .OrderBy(task => task.RequestedAtUtc)
                    .ThenBy(task => task.Subject.Title, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            },
            cancellationToken);
    }

    public Task SaveAsync(AiEnrichmentTask task, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var pendingEvents = task.UncommittedEvents.ToArray();
                if (pendingEvents.Length == 0)
                {
                    return;
                }

                var streamPath = GetStreamPath(task.Id);
                var document = await JsonFilePersistence.LoadAsync(
                    streamPath,
                    () => new AiEnrichmentTaskStreamDocument([]),
                    ct);

                var expectedVersion = task.Version - pendingEvents.Length;
                if (document.Events.Length != expectedVersion)
                {
                    throw new InvalidOperationException($"Optimistic concurrency violation for AI enrichment stream '{task.Id.Value:D}'.");
                }

                var updatedEvents = document.Events
                    .Concat(pendingEvents.Select(FromDomainEvent))
                    .ToArray();

                await JsonFilePersistence.SaveAsync(
                    streamPath,
                    new AiEnrichmentTaskStreamDocument(updatedEvents),
                    ct);

                task.DequeueUncommittedEvents();
            },
            cancellationToken);
    }

    private string GetStreamPath(AiEnrichmentTaskId id) => Path.Combine(_streamsDirectory, $"{id.Value:D}.json");

    private static AiEnrichmentTaskEventRecord FromDomainEvent(IDomainEvent domainEvent) =>
        domainEvent switch
        {
            AiEnrichmentRequested requested => new AiEnrichmentTaskEventRecord(
                "requested",
                requested.TaskId.Value,
                requested.Kind,
                requested.SubjectType,
                requested.SubjectId,
                requested.SubjectTitle,
                requested.Prompt,
                null,
                null,
                null,
                [],
                requested.OccurredAtUtc),
            AiEnrichmentProcessingStarted started => new AiEnrichmentTaskEventRecord(
                "started",
                started.TaskId.Value,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                [],
                started.OccurredAtUtc),
            AiEnrichmentCompleted completed => new AiEnrichmentTaskEventRecord(
                "completed",
                completed.TaskId.Value,
                null,
                null,
                null,
                null,
                null,
                completed.Model,
                completed.Content,
                null,
                completed.Citations.ToArray(),
                completed.OccurredAtUtc),
            AiEnrichmentFailed failed => new AiEnrichmentTaskEventRecord(
                "failed",
                failed.TaskId.Value,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                failed.Error,
                [],
                failed.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported AI enrichment domain event type '{domainEvent.GetType().Name}'.")
        };

    private static IDomainEvent ToDomainEvent(AiEnrichmentTaskEventRecord record) =>
        record.Type switch
        {
            "requested" => new AiEnrichmentRequested(
                new AiEnrichmentTaskId(record.TaskId),
                record.Kind ?? throw new InvalidOperationException("AI requested event is missing kind."),
                record.SubjectType ?? throw new InvalidOperationException("AI requested event is missing subject type."),
                record.SubjectId ?? throw new InvalidOperationException("AI requested event is missing subject identifier."),
                record.SubjectTitle ?? throw new InvalidOperationException("AI requested event is missing subject title."),
                record.Prompt ?? throw new InvalidOperationException("AI requested event is missing prompt."),
                record.OccurredAtUtc),
            "started" => new AiEnrichmentProcessingStarted(new AiEnrichmentTaskId(record.TaskId), record.OccurredAtUtc),
            "completed" => new AiEnrichmentCompleted(
                new AiEnrichmentTaskId(record.TaskId),
                record.Model ?? throw new InvalidOperationException("AI completed event is missing model."),
                record.Content ?? throw new InvalidOperationException("AI completed event is missing content."),
                record.Citations,
                record.OccurredAtUtc),
            "failed" => new AiEnrichmentFailed(
                new AiEnrichmentTaskId(record.TaskId),
                record.Error ?? throw new InvalidOperationException("AI failed event is missing error."),
                record.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported AI task event record type '{record.Type}'.")
        };

    private sealed record AiEnrichmentTaskStreamDocument(AiEnrichmentTaskEventRecord[] Events);

    private sealed record AiEnrichmentTaskEventRecord(
        string Type,
        Guid TaskId,
        string? Kind,
        string? SubjectType,
        Guid? SubjectId,
        string? SubjectTitle,
        string? Prompt,
        string? Model,
        string? Content,
        string? Error,
        string[] Citations,
        DateTimeOffset OccurredAtUtc);
}

public sealed class FileBackedAiEnrichmentTaskProjectionStore(string rootPath) : IAiEnrichmentTaskReadRepository, IAiEnrichmentTaskProjection
{
    private readonly string _rootPath = rootPath;
    private readonly string _projectionPath = Path.Combine(rootPath, "projection.json");

    public Task<IReadOnlyCollection<AiEnrichmentTaskReadModel>> GetTasksAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new AiEnrichmentTaskProjectionDocument([]),
                    ct);

                return (IReadOnlyCollection<AiEnrichmentTaskReadModel>)document.Tasks.ToArray();
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
                    () => new AiEnrichmentTaskProjectionDocument([]),
                    ct);

                var tasks = document.Tasks.ToDictionary(task => task.Id);
                foreach (var domainEvent in domainEvents)
                {
                    switch (domainEvent)
                    {
                        case AiEnrichmentRequested requested:
                            tasks[requested.TaskId.Value] = new AiEnrichmentTaskReadModel(
                                requested.TaskId.Value,
                                requested.Kind,
                                requested.SubjectType,
                                requested.SubjectId,
                                requested.SubjectTitle,
                                "queued",
                                null,
                                null,
                                null,
                                [],
                                requested.OccurredAtUtc,
                                null,
                                null,
                                null);
                            break;
                        case AiEnrichmentProcessingStarted started when tasks.TryGetValue(started.TaskId.Value, out var runningTask):
                            tasks[started.TaskId.Value] = runningTask with
                            {
                                Status = "running",
                                StartedAtUtc = started.OccurredAtUtc,
                                FailedAtUtc = null
                            };
                            break;
                        case AiEnrichmentCompleted completed when tasks.TryGetValue(completed.TaskId.Value, out var completedTask):
                            tasks[completed.TaskId.Value] = completedTask with
                            {
                                Status = "completed",
                                Model = completed.Model,
                                Content = completed.Content,
                                Error = null,
                                Citations = completed.Citations.ToArray(),
                                StartedAtUtc = completedTask.StartedAtUtc ?? completed.OccurredAtUtc,
                                CompletedAtUtc = completed.OccurredAtUtc,
                                FailedAtUtc = null
                            };
                            break;
                        case AiEnrichmentFailed failed when tasks.TryGetValue(failed.TaskId.Value, out var failedTask):
                            tasks[failed.TaskId.Value] = failedTask with
                            {
                                Status = "failed",
                                Error = failed.Error,
                                StartedAtUtc = failedTask.StartedAtUtc ?? failed.OccurredAtUtc,
                                CompletedAtUtc = null,
                                FailedAtUtc = failed.OccurredAtUtc
                            };
                            break;
                    }
                }

                await JsonFilePersistence.SaveAsync(
                    _projectionPath,
                    new AiEnrichmentTaskProjectionDocument(tasks.Values.ToArray()),
                    ct);
            },
            cancellationToken);
    }

    private sealed record AiEnrichmentTaskProjectionDocument(AiEnrichmentTaskReadModel[] Tasks);
}
