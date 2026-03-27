using System.Text.RegularExpressions;

namespace LawWatcher.SearchAndDiscovery.Infrastructure;

public static partial class SqlServerFullTextSearchConditionBuilder
{
    public static string? Build(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var terms = SearchTokenPattern()
            .Matches(query)
            .Select(match => match.Value.Trim())
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (terms.Length == 0)
        {
            return null;
        }

        return string.Join(
            " OR ",
            terms.Select(term => $"\"{Escape(term)}*\""));
    }

    private static string Escape(string term) => term.Replace("\"", "\"\"", StringComparison.Ordinal);

    [GeneratedRegex(@"[\p{L}\p{Nd}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex SearchTokenPattern();
}
