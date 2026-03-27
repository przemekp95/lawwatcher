namespace LawWatcher.IdentityAndAccess.Contracts;

public sealed record UpdateApiClientRequest(
    string Name,
    string? Token,
    IReadOnlyCollection<string> Scopes);
