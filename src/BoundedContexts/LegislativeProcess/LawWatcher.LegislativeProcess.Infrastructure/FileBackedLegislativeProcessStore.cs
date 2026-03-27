using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.LegislativeProcess.Application;
using LawWatcher.LegislativeProcess.Domain.Processes;

namespace LawWatcher.LegislativeProcess.Infrastructure;

public sealed class FileBackedLegislativeProcessRepository(string rootPath) : ILegislativeProcessRepository
{
    private readonly string _rootPath = rootPath;
    private readonly string _streamsDirectory = Path.Combine(rootPath, "streams");

    public Task<Domain.Processes.LegislativeProcess?> GetAsync(LegislativeProcessId id, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    GetStreamPath(id),
                    () => new LegislativeProcessStreamDocument([]),
                    ct);

                return document.Events.Length == 0
                    ? null
                    : Domain.Processes.LegislativeProcess.Rehydrate(document.Events.Select(ToDomainEvent).ToArray());
            },
            cancellationToken);
    }

    public Task SaveAsync(Domain.Processes.LegislativeProcess process, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var pendingEvents = process.UncommittedEvents.ToArray();
                if (pendingEvents.Length == 0)
                {
                    return;
                }

                var streamPath = GetStreamPath(process.Id);
                var document = await JsonFilePersistence.LoadAsync(
                    streamPath,
                    () => new LegislativeProcessStreamDocument([]),
                    ct);

                var expectedVersion = process.Version - pendingEvents.Length;
                if (document.Events.Length != expectedVersion)
                {
                    throw new InvalidOperationException($"Optimistic concurrency violation for legislative process stream '{process.Id.Value:D}'.");
                }

                await JsonFilePersistence.SaveAsync(
                    streamPath,
                    new LegislativeProcessStreamDocument(document.Events.Concat(pendingEvents.Select(FromDomainEvent)).ToArray()),
                    ct);

                process.DequeueUncommittedEvents();
            },
            cancellationToken);
    }

    private string GetStreamPath(LegislativeProcessId id) => Path.Combine(_streamsDirectory, $"{id.Value:D}.json");

    private static LegislativeProcessEventRecord FromDomainEvent(IDomainEvent domainEvent) =>
        domainEvent switch
        {
            LegislativeProcessStarted started => new LegislativeProcessEventRecord(
                "started",
                started.ProcessId.Value,
                started.BillId,
                started.BillTitle,
                started.BillExternalId,
                started.StageCode,
                started.StageLabel,
                started.StageOccurredOn,
                started.OccurredAtUtc),
            LegislativeStageRecorded recorded => new LegislativeProcessEventRecord(
                "stage-recorded",
                recorded.ProcessId.Value,
                null,
                null,
                null,
                recorded.StageCode,
                recorded.StageLabel,
                recorded.StageOccurredOn,
                recorded.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported legislative process domain event type '{domainEvent.GetType().Name}'.")
        };

    private static IDomainEvent ToDomainEvent(LegislativeProcessEventRecord record) =>
        record.Type switch
        {
            "started" => new LegislativeProcessStarted(
                new LegislativeProcessId(record.ProcessId),
                record.BillId ?? throw new InvalidOperationException("Legislative process started event is missing bill id."),
                record.BillTitle ?? throw new InvalidOperationException("Legislative process started event is missing bill title."),
                record.BillExternalId ?? throw new InvalidOperationException("Legislative process started event is missing bill external id."),
                record.StageCode ?? throw new InvalidOperationException("Legislative process started event is missing stage code."),
                record.StageLabel ?? throw new InvalidOperationException("Legislative process started event is missing stage label."),
                record.StageOccurredOn ?? throw new InvalidOperationException("Legislative process started event is missing occurred-on date."),
                record.OccurredAtUtc),
            "stage-recorded" => new LegislativeStageRecorded(
                new LegislativeProcessId(record.ProcessId),
                record.StageCode ?? throw new InvalidOperationException("Legislative stage recorded event is missing stage code."),
                record.StageLabel ?? throw new InvalidOperationException("Legislative stage recorded event is missing stage label."),
                record.StageOccurredOn ?? throw new InvalidOperationException("Legislative stage recorded event is missing occurred-on date."),
                record.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported legislative process event record type '{record.Type}'.")
        };

    private sealed record LegislativeProcessStreamDocument(LegislativeProcessEventRecord[] Events);

    private sealed record LegislativeProcessEventRecord(
        string Type,
        Guid ProcessId,
        Guid? BillId,
        string? BillTitle,
        string? BillExternalId,
        string? StageCode,
        string? StageLabel,
        DateOnly? StageOccurredOn,
        DateTimeOffset OccurredAtUtc);
}

public sealed class FileBackedLegislativeProcessProjectionStore(string rootPath) : ILegislativeProcessReadRepository, ILegislativeProcessProjection
{
    private readonly string _rootPath = rootPath;
    private readonly string _projectionPath = Path.Combine(rootPath, "projection.json");

    public Task<IReadOnlyCollection<LegislativeProcessReadModel>> GetProcessesAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new LegislativeProcessProjectionDocument([]),
                    ct);

                return (IReadOnlyCollection<LegislativeProcessReadModel>)document.Processes
                    .Select(record => new LegislativeProcessReadModel(
                        record.Id,
                        record.BillId,
                        record.BillTitle,
                        record.BillExternalId,
                        record.CurrentStageCode,
                        record.CurrentStageLabel,
                        record.LastUpdatedOn,
                        record.Stages.Select(stage => new LegislativeStageReadModel(stage.Code, stage.Label, stage.OccurredOn)).ToArray()))
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
                    () => new LegislativeProcessProjectionDocument([]),
                    ct);

                var processes = document.Processes.ToDictionary(process => process.Id);
                foreach (var domainEvent in domainEvents)
                {
                    switch (domainEvent)
                    {
                        case LegislativeProcessStarted started:
                            processes[started.ProcessId.Value] = LegislativeProcessProjectionRecord.From(started);
                            break;
                        case LegislativeStageRecorded recorded when processes.TryGetValue(recorded.ProcessId.Value, out var existing):
                            processes[recorded.ProcessId.Value] = existing.RecordStage(recorded.StageCode, recorded.StageLabel, recorded.StageOccurredOn);
                            break;
                    }
                }

                await JsonFilePersistence.SaveAsync(
                    _projectionPath,
                    new LegislativeProcessProjectionDocument(
                        processes.Values
                            .OrderByDescending(process => process.LastUpdatedOn)
                            .ThenBy(process => process.BillTitle, StringComparer.OrdinalIgnoreCase)
                            .ToArray()),
                    ct);
            },
            cancellationToken);
    }

    private sealed record LegislativeProcessProjectionDocument(LegislativeProcessProjectionRecord[] Processes);

    private sealed record LegislativeProcessProjectionRecord(
        Guid Id,
        Guid BillId,
        string BillTitle,
        string BillExternalId,
        string CurrentStageCode,
        string CurrentStageLabel,
        DateOnly LastUpdatedOn,
        LegislativeStageProjectionRecord[] Stages)
    {
        public static LegislativeProcessProjectionRecord From(LegislativeProcessStarted started)
        {
            return new LegislativeProcessProjectionRecord(
                started.ProcessId.Value,
                started.BillId,
                started.BillTitle,
                started.BillExternalId,
                started.StageCode,
                started.StageLabel,
                started.StageOccurredOn,
                [new LegislativeStageProjectionRecord(started.StageCode, started.StageLabel, started.StageOccurredOn)]);
        }

        public LegislativeProcessProjectionRecord RecordStage(string code, string label, DateOnly occurredOn)
        {
            var exists = Stages.Any(stage =>
                stage.Code.Equals(code, StringComparison.OrdinalIgnoreCase) &&
                stage.Label.Equals(label, StringComparison.OrdinalIgnoreCase) &&
                stage.OccurredOn == occurredOn);

            if (exists)
            {
                return this;
            }

            var updatedStages = Stages
                .Append(new LegislativeStageProjectionRecord(code, label, occurredOn))
                .ToArray();

            var current = updatedStages
                .OrderByDescending(stage => stage.OccurredOn)
                .ThenBy(stage => stage.Code, StringComparer.OrdinalIgnoreCase)
                .First();

            return this with
            {
                CurrentStageCode = current.Code,
                CurrentStageLabel = current.Label,
                LastUpdatedOn = current.OccurredOn,
                Stages = updatedStages
            };
        }
    }

    private sealed record LegislativeStageProjectionRecord(
        string Code,
        string Label,
        DateOnly OccurredOn);
}
