using LawWatcher.SearchAndDiscovery.Contracts;
using LawWatcher.SearchAndDiscovery.Domain;

namespace LawWatcher.SearchAndDiscovery.Application;

public sealed record SearchSourceDocument(
    string Id,
    string Title,
    SearchDocumentKind Kind,
    string Snippet,
    IReadOnlyCollection<string> Keywords);

public interface ISearchDocumentIndex
{
    Task ReplaceAllAsync(IReadOnlyCollection<IndexedSearchDocument> documents, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<SearchMatch>> SearchAsync(string query, CancellationToken cancellationToken);
}

public sealed class SearchIndexingService(ISearchDocumentIndex documentIndex)
{
    public Task ReplaceAllAsync(IReadOnlyCollection<SearchSourceDocument> documents, CancellationToken cancellationToken)
    {
        var indexedDocuments = documents
            .Select(document => IndexedSearchDocument.Create(
                document.Id,
                document.Title,
                document.Kind,
                document.Snippet,
                document.Keywords))
            .ToArray();

        return documentIndex.ReplaceAllAsync(indexedDocuments, cancellationToken);
    }
}

public sealed class SearchQueryService(ISearchDocumentIndex documentIndex)
{
    public async Task<SearchQueryResult> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var matches = await documentIndex.SearchAsync(query, cancellationToken);

        var hits = matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Document.Title, StringComparer.OrdinalIgnoreCase)
            .Select(match => new SearchHitResult(
                match.Document.Id,
                match.Document.Title,
                match.Document.Kind.ToString().ToLowerInvariant(),
                match.Document.Snippet))
            .ToArray();

        return new SearchQueryResult(query, hits);
    }
}
