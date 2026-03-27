namespace LawWatcher.LegislativeProcess.Contracts;

public sealed record LegislativeStageResponse(
    string Code,
    string Label,
    DateOnly OccurredOn);

public sealed record LegislativeProcessResponse(
    Guid Id,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    string CurrentStageCode,
    string CurrentStageLabel,
    DateOnly LastUpdatedOn,
    int StageCount,
    IReadOnlyCollection<LegislativeStageResponse> Stages);
