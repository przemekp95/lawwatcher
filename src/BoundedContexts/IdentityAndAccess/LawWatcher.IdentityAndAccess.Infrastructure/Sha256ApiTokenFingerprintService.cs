using System.Security.Cryptography;
using System.Text;
using LawWatcher.IdentityAndAccess.Application;

namespace LawWatcher.IdentityAndAccess.Infrastructure;

public sealed class Sha256ApiTokenFingerprintService : IApiTokenFingerprintService
{
    public string CreateFingerprint(string token)
    {
        var normalized = token.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("API token cannot be empty.", nameof(token));
        }

        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return $"sha256:{Convert.ToHexStringLower(hash)}";
    }
}
