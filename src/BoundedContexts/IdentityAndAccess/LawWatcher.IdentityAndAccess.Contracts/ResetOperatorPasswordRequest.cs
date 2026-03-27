namespace LawWatcher.IdentityAndAccess.Contracts;

public sealed record ResetOperatorPasswordRequest(
    string NewPassword);
