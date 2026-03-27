using LawWatcher.BuildingBlocks.Messaging;

namespace LawWatcher.TaxonomyAndProfiles.Contracts;

public sealed record ProfileSubscriptionCreatedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid SubscriptionId,
    Guid ProfileId,
    string ProfileName,
    string Subscriber,
    string ChannelCode,
    string AlertPolicyCode,
    TimeSpan? DigestInterval) : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record ProfileSubscriptionAlertPolicyChangedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid SubscriptionId,
    string AlertPolicyCode,
    TimeSpan? DigestInterval) : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record ProfileSubscriptionDeactivatedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid SubscriptionId) : IntegrationEvent(EventId, OccurredAtUtc);
