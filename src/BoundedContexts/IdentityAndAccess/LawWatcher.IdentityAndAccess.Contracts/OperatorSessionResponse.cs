namespace LawWatcher.IdentityAndAccess.Contracts;

public sealed record OperatorSessionResponse(
    bool IsAuthenticated,
    Guid? OperatorId,
    string? Email,
    string? DisplayName,
    IReadOnlyCollection<string> Permissions,
    string CsrfRequestToken);
