using LawWatcher.BuildingBlocks.Messaging;

namespace LawWatcher.LegalCorpus.Contracts;

public sealed record PublishedActRegisteredIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid ActId,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    string Eli,
    string Title,
    DateOnly PublishedOn,
    DateOnly? EffectiveFrom) : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record ActArtifactAttachedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid ActId,
    string Kind,
    string ObjectKey) : IntegrationEvent(EventId, OccurredAtUtc);
