using System.Security.Claims;

namespace LawWatcher.Api.Runtime;

public static class OperatorCookieAuthenticationDefaults
{
    public const string Scheme = "lawwatcher-operator-cookie";

    public const string PermissionClaimType = "lawwatcher.permission";

    public const string OperatorIdClaimType = ClaimTypes.NameIdentifier;
}
