namespace LawWatcher.IdentityAndAccess.Contracts;

public sealed record OperatorLoginRequest(
    string Email,
    string Password);
