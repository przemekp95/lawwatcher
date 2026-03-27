using LawWatcher.BuildingBlocks.Messaging;

namespace LawWatcher.LegislativeProcess.Contracts;

public sealed record LegislativeProcessStartedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid ProcessId,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    string StageCode,
    string StageLabel,
    DateOnly StageOccurredOn) : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record LegislativeStageRecordedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid ProcessId,
    string StageCode,
    string StageLabel,
    DateOnly StageOccurredOn) : IntegrationEvent(EventId, OccurredAtUtc);
