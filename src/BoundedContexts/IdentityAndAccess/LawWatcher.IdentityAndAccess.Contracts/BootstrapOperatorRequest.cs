namespace LawWatcher.IdentityAndAccess.Contracts;

public sealed record BootstrapOperatorRequest(
    string Email,
    string DisplayName,
    string Password);
