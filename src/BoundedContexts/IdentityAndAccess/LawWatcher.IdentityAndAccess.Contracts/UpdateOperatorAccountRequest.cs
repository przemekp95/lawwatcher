namespace LawWatcher.IdentityAndAccess.Contracts;

public sealed record UpdateOperatorAccountRequest(
    string DisplayName,
    IReadOnlyCollection<string> Permissions);
