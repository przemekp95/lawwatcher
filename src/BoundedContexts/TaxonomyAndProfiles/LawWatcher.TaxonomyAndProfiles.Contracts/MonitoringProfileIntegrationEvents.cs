using LawWatcher.BuildingBlocks.Messaging;

namespace LawWatcher.TaxonomyAndProfiles.Contracts;

public sealed record MonitoringProfileCreatedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid ProfileId,
    string Name,
    string AlertPolicyCode,
    TimeSpan? DigestInterval) : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record MonitoringProfileRuleAddedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid ProfileId,
    string RuleKind,
    string RuleValue) : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record MonitoringProfileAlertPolicyChangedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid ProfileId,
    string AlertPolicyCode,
    TimeSpan? DigestInterval) : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record MonitoringProfileDeactivatedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid ProfileId) : IntegrationEvent(EventId, OccurredAtUtc);
