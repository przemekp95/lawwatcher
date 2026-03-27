namespace LawWatcher.IdentityAndAccess.Contracts;

public sealed record CreateApiClientRequest(
    string Name,
    string ClientIdentifier,
    string Token,
    IReadOnlyCollection<string> Scopes);
