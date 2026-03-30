namespace LawWatcher.IdentityAndAccess.Contracts;

public sealed record BootstrapApiClientResponse(
    Guid Id,
    string Name,
    string ClientIdentifier,
    string Token,
    IReadOnlyCollection<string> Scopes);
