using LawWatcher.BuildingBlocks.Messaging;

namespace LawWatcher.AiEnrichment.Contracts;

public sealed record DocumentTextExtractedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    string OwnerType,
    Guid OwnerId,
    string SourceKind,
    string SourceBucket,
    string SourceObjectKey,
    string DerivedBucket,
    string DerivedObjectKey) : IntegrationEvent(EventId, OccurredAtUtc);
