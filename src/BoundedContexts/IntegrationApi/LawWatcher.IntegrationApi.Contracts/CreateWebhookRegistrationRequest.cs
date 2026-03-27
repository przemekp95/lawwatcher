namespace LawWatcher.IntegrationApi.Contracts;

public sealed record CreateWebhookRegistrationRequest(
    string Name,
    string CallbackUrl,
    IReadOnlyCollection<string> EventTypes);
