namespace LawWatcher.SearchAndDiscovery.Contracts;

public sealed record SearchHitResult(
    string Id,
    string Title,
    string Type,
    string Snippet);

public sealed record SearchQueryResult(
    string Query,
    IReadOnlyCollection<SearchHitResult> Hits);
