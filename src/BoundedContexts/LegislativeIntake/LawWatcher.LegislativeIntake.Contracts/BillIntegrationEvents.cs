using LawWatcher.BuildingBlocks.Messaging;

namespace LawWatcher.LegislativeIntake.Contracts;

public sealed record BillImportedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid BillId,
    string SourceSystem,
    string ExternalId,
    string SourceUrl,
    string Title,
    DateOnly SubmittedOn) : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record BillDocumentAttachedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid BillId,
    string Kind,
    string ObjectKey) : IntegrationEvent(EventId, OccurredAtUtc);
