using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.LegislativeProcess.Contracts;
using LawWatcher.LegislativeProcess.Domain.Processes;

namespace LawWatcher.LegislativeProcess.Application;

public sealed record StartLegislativeProcessCommand(
    Guid ProcessId,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    LegislativeStage InitialStage) : Command;

public sealed record RecordLegislativeStageCommand(
    Guid ProcessId,
    LegislativeStage Stage) : Command;

public sealed record LegislativeStageReadModel(
    string Code,
    string Label,
    DateOnly OccurredOn);

public sealed record LegislativeProcessReadModel(
    Guid Id,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    string CurrentStageCode,
    string CurrentStageLabel,
    DateOnly LastUpdatedOn,
    IReadOnlyCollection<LegislativeStageReadModel> Stages);

public interface ILegislativeProcessRepository
{
    Task<Domain.Processes.LegislativeProcess?> GetAsync(LegislativeProcessId id, CancellationToken cancellationToken);

    Task SaveAsync(Domain.Processes.LegislativeProcess process, CancellationToken cancellationToken);
}

public interface ILegislativeProcessOutboxWriter
{
    Task SaveAsync(
        Domain.Processes.LegislativeProcess process,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken);
}

public interface ILegislativeProcessReadRepository
{
    Task<IReadOnlyCollection<LegislativeProcessReadModel>> GetProcessesAsync(CancellationToken cancellationToken);
}

public interface ILegislativeProcessProjection
{
    Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}

public sealed class LegislativeProcessCommandService(
    ILegislativeProcessRepository repository,
    ILegislativeProcessProjection projection)
{
    public async Task StartAsync(StartLegislativeProcessCommand command, CancellationToken cancellationToken)
    {
        var processId = new LegislativeProcessId(command.ProcessId);
        var existing = await repository.GetAsync(processId, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Legislative process '{command.ProcessId}' has already been started.");
        }

        var process = Domain.Processes.LegislativeProcess.Start(
            processId,
            LinkedBillReference.Create(command.BillId, command.BillTitle, command.BillExternalId),
            command.InitialStage,
            command.RequestedAtUtc);

        await SaveAndProjectAsync(process, cancellationToken);
    }

    public async Task RecordStageAsync(RecordLegislativeStageCommand command, CancellationToken cancellationToken)
    {
        var process = await repository.GetAsync(new LegislativeProcessId(command.ProcessId), cancellationToken)
            ?? throw new InvalidOperationException($"Legislative process '{command.ProcessId}' was not found.");

        process.RecordStage(command.Stage, command.RequestedAtUtc);
        await SaveAndProjectAsync(process, cancellationToken);
    }

    private async Task SaveAndProjectAsync(Domain.Processes.LegislativeProcess process, CancellationToken cancellationToken)
    {
        var pendingEvents = process.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var integrationEvents = new List<IIntegrationEvent>(pendingEvents.Length);
        foreach (var domainEvent in pendingEvents)
        {
            switch (domainEvent)
            {
                case LegislativeProcessStarted started:
                    integrationEvents.Add(new LegislativeProcessStartedIntegrationEvent(
                        started.EventId,
                        started.OccurredAtUtc,
                        started.ProcessId.Value,
                        started.BillId,
                        started.BillTitle,
                        started.BillExternalId,
                        started.StageCode,
                        started.StageLabel,
                        started.StageOccurredOn));
                    break;
                case LegislativeStageRecorded recorded:
                    integrationEvents.Add(new LegislativeStageRecordedIntegrationEvent(
                        recorded.EventId,
                        recorded.OccurredAtUtc,
                        recorded.ProcessId.Value,
                        recorded.StageCode,
                        recorded.StageLabel,
                        recorded.StageOccurredOn));
                    break;
            }
        }

        if (repository is ILegislativeProcessOutboxWriter outboxWriter && integrationEvents.Count != 0)
        {
            await outboxWriter.SaveAsync(process, integrationEvents, cancellationToken);
        }
        else
        {
            await repository.SaveAsync(process, cancellationToken);
        }

        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }
}

public sealed class ProcessesQueryService(ILegislativeProcessReadRepository repository)
{
    public async Task<IReadOnlyList<LegislativeProcessResponse>> GetProcessesAsync(CancellationToken cancellationToken)
    {
        var processes = await repository.GetProcessesAsync(cancellationToken);

        return processes
            .OrderByDescending(process => process.LastUpdatedOn)
            .ThenBy(process => process.BillTitle, StringComparer.OrdinalIgnoreCase)
            .Select(process => new LegislativeProcessResponse(
                process.Id,
                process.BillId,
                process.BillTitle,
                process.BillExternalId,
                process.CurrentStageCode,
                process.CurrentStageLabel,
                process.LastUpdatedOn,
                process.Stages.Count,
                process.Stages
                    .OrderBy(stage => stage.OccurredOn)
                    .ThenBy(stage => stage.Code, StringComparer.OrdinalIgnoreCase)
                    .Select(stage => new LegislativeStageResponse(stage.Code, stage.Label, stage.OccurredOn))
                    .ToArray()))
            .ToArray();
    }
}
