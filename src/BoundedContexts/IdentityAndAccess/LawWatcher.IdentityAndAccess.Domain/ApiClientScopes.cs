namespace LawWatcher.IdentityAndAccess.Domain.ApiClients;

public static class ApiClientScopeCatalog
{
    public const string IntegrationRead = "integration:read";

    public static string Normalize(string value, string paramName, string label)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"{label} cannot be empty.", paramName);
        }

        return normalized;
    }

    public static string[] NormalizeMany(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return values
            .Select(value => Normalize(value, nameof(values), "API scope"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
