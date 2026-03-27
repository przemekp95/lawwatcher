using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using LawWatcher.IdentityAndAccess.Application;

namespace LawWatcher.IdentityAndAccess.Infrastructure;

public sealed class Pbkdf2OperatorPasswordHasher(
    int iterations = 100_000,
    int saltSize = 16,
    int keySize = 32) : IOperatorPasswordHasher
{
    private const string FormatPrefix = "pbkdf2-sha256";
    private readonly int _iterations = iterations > 0 ? iterations : throw new ArgumentOutOfRangeException(nameof(iterations));
    private readonly int _saltSize = saltSize > 0 ? saltSize : throw new ArgumentOutOfRangeException(nameof(saltSize));
    private readonly int _keySize = keySize > 0 ? keySize : throw new ArgumentOutOfRangeException(nameof(keySize));

    public string Hash(string password)
    {
        var normalizedPassword = NormalizePassword(password, nameof(password), "Password");
        var salt = RandomNumberGenerator.GetBytes(_saltSize);
        var derivedKey = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(normalizedPassword),
            salt,
            _iterations,
            HashAlgorithmName.SHA256,
            _keySize);
        return $"{FormatPrefix}${_iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(derivedKey)}";
    }

    public bool Verify(string password, string passwordHash)
    {
        var normalizedPassword = NormalizePassword(password, nameof(password), "Password");
        var normalizedHash = passwordHash.Trim();
        if (normalizedHash.Length == 0)
        {
            return false;
        }

        var segments = normalizedHash.Split('$', StringSplitOptions.None);
        if (segments.Length != 4 ||
            !string.Equals(segments[0], FormatPrefix, StringComparison.Ordinal) ||
            !int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) ||
            iterations <= 0)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(segments[2]);
            var expectedHash = Convert.FromBase64String(segments[3]);
            var actualHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(normalizedPassword),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string NormalizePassword(string value, string paramName, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{label} cannot be empty.", paramName);
        }

        return value;
    }
}
