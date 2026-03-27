using LawWatcher.BuildingBlocks.Messaging;

namespace LawWatcher.IntegrationApi.Contracts;

public sealed record WebhookRegisteredIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid RegistrationId,
    string Name,
    string CallbackUrl,
    IReadOnlyCollection<string> EventTypes) : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record WebhookUpdatedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid RegistrationId,
    string Name,
    string CallbackUrl,
    IReadOnlyCollection<string> EventTypes) : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record WebhookDeactivatedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Guid RegistrationId) : IntegrationEvent(EventId, OccurredAtUtc);
