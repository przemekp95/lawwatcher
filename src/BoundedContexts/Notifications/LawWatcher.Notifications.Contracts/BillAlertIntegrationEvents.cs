using LawWatcher.BuildingBlocks.Messaging;

namespace LawWatcher.Notifications.Contracts;

public sealed record BillAlertCreatedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid AlertId,
    Guid ProfileId,
    string ProfileName,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    DateOnly BillSubmittedOn,
    string AlertPolicy,
    IReadOnlyCollection<string> MatchedKeywords) : IntegrationEvent(EventId, OccurredAtUtc);
