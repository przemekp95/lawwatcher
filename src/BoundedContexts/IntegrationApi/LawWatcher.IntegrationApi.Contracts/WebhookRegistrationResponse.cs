namespace LawWatcher.IntegrationApi.Contracts;

public sealed record WebhookRegistrationResponse(
    Guid Id,
    string Name,
    string CallbackUrl,
    IReadOnlyCollection<string> EventTypes,
    bool IsActive);
