namespace LawWatcher.IntegrationApi.Contracts;

public sealed record UpdateWebhookRegistrationRequest(
    string Name,
    string CallbackUrl,
    IReadOnlyCollection<string> EventTypes);
