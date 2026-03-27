namespace LawWatcher.IntegrationApi.Contracts;

public sealed record WebhookEventDispatchResponse(
    Guid AlertId,
    Guid RegistrationId,
    string EventType,
    string CallbackUrl,
    DateTimeOffset DispatchedAtUtc);
