using LawWatcher.BuildingBlocks.Messaging;

namespace LawWatcher.IntegrationApi.Contracts;

public sealed record ReplayRequestedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid ReplayRequestId,
    string Scope,
    string RequestedBy) : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record BackfillRequestedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid BackfillRequestId,
    string Source,
    string Scope,
    DateOnly RequestedFrom,
    DateOnly? RequestedTo,
    string RequestedBy) : IntegrationEvent(EventId, OccurredAtUtc);
