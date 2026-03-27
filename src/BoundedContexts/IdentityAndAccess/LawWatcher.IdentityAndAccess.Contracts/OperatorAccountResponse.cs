namespace LawWatcher.IdentityAndAccess.Contracts;

public sealed record OperatorAccountResponse(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyCollection<string> Permissions,
    bool IsActive,
    DateTimeOffset RegisteredAtUtc);
