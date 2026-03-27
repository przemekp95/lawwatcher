namespace LawWatcher.SearchAndDiscovery.Domain;

public enum SearchDocumentKind
{
    Bill = 1,
    Act = 2,
    Process = 3,
    Profile = 4,
    Alert = 5
}

public sealed record SearchMatch(IndexedSearchDocument Document, int Score);

public sealed class IndexedSearchDocument
{
    private readonly string _normalizedTitle;
    private readonly string _normalizedSnippet;
    private readonly string[] _normalizedKeywords;

    private IndexedSearchDocument(
        string id,
        string title,
        SearchDocumentKind kind,
        string snippet,
        IReadOnlyCollection<string> keywords)
    {
        Id = NormalizeRequired(id, nameof(id));
        Title = NormalizeRequired(title, nameof(title));
        Kind = kind;
        Snippet = NormalizeRequired(snippet, nameof(snippet));
        Keywords = keywords;

        _normalizedTitle = NormalizeForSearch(Title);
        _normalizedSnippet = NormalizeForSearch(Snippet);
        _normalizedKeywords = keywords.Select(NormalizeForSearch).ToArray();
    }

    public string Id { get; }

    public string Title { get; }

    public SearchDocumentKind Kind { get; }

    public string Snippet { get; }

    public IReadOnlyCollection<string> Keywords { get; }

    public static IndexedSearchDocument Create(
        string id,
        string title,
        SearchDocumentKind kind,
        string snippet,
        IEnumerable<string> keywords)
    {
        ArgumentNullException.ThrowIfNull(keywords);

        var normalizedKeywords = keywords
            .Select(keyword => NormalizeRequired(keyword, nameof(keywords)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(keyword => keyword, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new IndexedSearchDocument(id, title, kind, snippet, normalizedKeywords);
    }

    public SearchMatch? Match(string query)
    {
        var normalizedTerms = Tokenize(query);
        if (normalizedTerms.Length == 0)
        {
            return null;
        }

        var score = BaseScoreForKind(Kind);

        var matched = false;

        foreach (var term in normalizedTerms)
        {
            if (_normalizedTitle.Contains(term, StringComparison.Ordinal))
            {
                score += 40;
                matched = true;
            }

            if (_normalizedSnippet.Contains(term, StringComparison.Ordinal))
            {
                score += 20;
                matched = true;
            }

            if (_normalizedKeywords.Any(keyword => keyword.Contains(term, StringComparison.Ordinal)))
            {
                score += 30;
                matched = true;
            }
        }

        return matched ? new SearchMatch(this, score) : null;
    }

    public static int BaseScoreForKind(SearchDocumentKind kind) =>
        kind switch
        {
            SearchDocumentKind.Bill => 140,
            SearchDocumentKind.Act => 110,
            SearchDocumentKind.Process => 90,
            SearchDocumentKind.Profile => 60,
            SearchDocumentKind.Alert => 20,
            _ => 0
        };

    private static string NormalizeRequired(string value, string paramName)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Value cannot be empty.", paramName);
        }

        return normalized;
    }

    private static string NormalizeForSearch(string value) => value.Trim().ToUpperInvariant();

    private static string[] Tokenize(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeForSearch)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
