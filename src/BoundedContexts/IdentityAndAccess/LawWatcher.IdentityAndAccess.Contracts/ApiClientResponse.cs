namespace LawWatcher.IdentityAndAccess.Contracts;

public sealed record ApiClientResponse(
    Guid Id,
    string Name,
    string ClientIdentifier,
    string TokenFingerprint,
    IReadOnlyCollection<string> Scopes,
    bool IsActive,
    DateTimeOffset RegisteredAtUtc);
