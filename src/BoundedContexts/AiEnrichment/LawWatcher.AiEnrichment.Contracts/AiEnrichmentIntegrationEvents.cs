using LawWatcher.BuildingBlocks.Messaging;

namespace LawWatcher.AiEnrichment.Contracts;

public sealed record AiEnrichmentRequestedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid TaskId,
    string Kind,
    string SubjectType,
    Guid SubjectId,
    string SubjectTitle) : IntegrationEvent(EventId, OccurredAtUtc);
