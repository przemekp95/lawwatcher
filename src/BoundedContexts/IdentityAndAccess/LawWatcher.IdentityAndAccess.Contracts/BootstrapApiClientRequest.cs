namespace LawWatcher.IdentityAndAccess.Contracts;

public sealed record BootstrapApiClientRequest(
    string Name,
    string ClientIdentifier,
    IReadOnlyCollection<string> Scopes);
