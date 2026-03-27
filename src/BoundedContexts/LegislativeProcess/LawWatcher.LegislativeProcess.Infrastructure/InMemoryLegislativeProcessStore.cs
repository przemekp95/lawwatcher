using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.LegislativeProcess.Application;
using LawWatcher.LegislativeProcess.Domain.Processes;

namespace LawWatcher.LegislativeProcess.Infrastructure;

public sealed class InMemoryLegislativeProcessRepository : ILegislativeProcessRepository
{
    private readonly Dictionary<string, List<IDomainEvent>> _streams = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<Domain.Processes.LegislativeProcess?> GetAsync(LegislativeProcessId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_streams.TryGetValue(GetStreamId(id), out var history))
            {
                return Task.FromResult<Domain.Processes.LegislativeProcess?>(null);
            }

            return Task.FromResult<Domain.Processes.LegislativeProcess?>(Domain.Processes.LegislativeProcess.Rehydrate(history.ToArray()));
        }
    }

    public Task SaveAsync(Domain.Processes.LegislativeProcess process, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pendingEvents = process.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return Task.CompletedTask;
        }

        var streamId = GetStreamId(process.Id);
        var expectedVersion = process.Version - pendingEvents.Length;

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

        process.DequeueUncommittedEvents();
        return Task.CompletedTask;
    }

    private static string GetStreamId(LegislativeProcessId id) => $"legislative-process-{id.Value:D}";
}

public sealed class InMemoryLegislativeProcessProjectionStore : ILegislativeProcessReadRepository, ILegislativeProcessProjection
{
    private readonly Dictionary<Guid, ProjectionState> _processes = new();
    private readonly Lock _gate = new();

    public Task<IReadOnlyCollection<LegislativeProcessReadModel>> GetProcessesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyCollection<LegislativeProcessReadModel>>(
                _processes.Values
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
                    case LegislativeProcessStarted started:
                        _processes[started.ProcessId.Value] = ProjectionState.From(started);
                        break;
                    case LegislativeStageRecorded recorded when _processes.TryGetValue(recorded.ProcessId.Value, out var existing):
                        existing.RecordStage(recorded.StageCode, recorded.StageLabel, recorded.StageOccurredOn);
                        break;
                }
            }
        }

        return Task.CompletedTask;
    }

    private sealed class ProjectionState
    {
        private readonly List<LegislativeStageReadModel> _stages = [];

        private ProjectionState(Guid id, Guid billId, string billTitle, string billExternalId)
        {
            Id = id;
            BillId = billId;
            BillTitle = billTitle;
            BillExternalId = billExternalId;
        }

        public Guid Id { get; }

        public Guid BillId { get; }

        public string BillTitle { get; }

        public string BillExternalId { get; }

        public string CurrentStageCode { get; private set; } = "submitted";

        public string CurrentStageLabel { get; private set; } = "Submitted";

        public DateOnly LastUpdatedOn { get; private set; } = new DateOnly(2000, 01, 01);

        public static ProjectionState From(LegislativeProcessStarted started)
        {
            var state = new ProjectionState(started.ProcessId.Value, started.BillId, started.BillTitle, started.BillExternalId);
            state.RecordStage(started.StageCode, started.StageLabel, started.StageOccurredOn);
            return state;
        }

        public void RecordStage(string code, string label, DateOnly occurredOn)
        {
            if (_stages.Any(stage =>
                stage.Code.Equals(code, StringComparison.OrdinalIgnoreCase) &&
                stage.OccurredOn == occurredOn &&
                stage.Label.Equals(label, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _stages.Add(new LegislativeStageReadModel(code, label, occurredOn));

            var current = _stages
                .OrderByDescending(stage => stage.OccurredOn)
                .ThenBy(stage => stage.Code, StringComparer.OrdinalIgnoreCase)
                .First();

            CurrentStageCode = current.Code;
            CurrentStageLabel = current.Label;
            LastUpdatedOn = current.OccurredOn;
        }

        public LegislativeProcessReadModel ToReadModel()
        {
            return new LegislativeProcessReadModel(
                Id,
                BillId,
                BillTitle,
                BillExternalId,
                CurrentStageCode,
                CurrentStageLabel,
                LastUpdatedOn,
                _stages.ToArray());
        }
    }
}
