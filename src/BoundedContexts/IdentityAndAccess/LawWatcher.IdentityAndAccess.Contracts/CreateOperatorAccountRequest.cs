namespace LawWatcher.IdentityAndAccess.Contracts;

public sealed record CreateOperatorAccountRequest(
    string Email,
    string DisplayName,
    string Password,
    IReadOnlyCollection<string> Permissions);
