using LawWatcher.SearchAndDiscovery.Application;
using LawWatcher.SearchAndDiscovery.Domain;
using LawWatcher.BuildingBlocks.Ports;

namespace LawWatcher.SearchAndDiscovery.Infrastructure;

public sealed class InMemorySearchDocumentIndex : ISearchDocumentIndex, ISearchIndexer
{
    private readonly Lock _gate = new();
    private Dictionary<string, IndexedSearchDocument> _documents = new(StringComparer.OrdinalIgnoreCase);

    public Task ReplaceAllAsync(IReadOnlyCollection<IndexedSearchDocument> documents, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _documents = documents.ToDictionary(document => document.Id, StringComparer.OrdinalIgnoreCase);
        }

        return Task.CompletedTask;
    }

    public Task IndexAsync(string documentId, string title, string content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var indexedDocument = IndexedSearchDocument.Create(
            documentId,
            title,
            GuessKind(documentId),
            content,
            ExtractKeywords(title, content));

        lock (_gate)
        {
            _documents[indexedDocument.Id] = indexedDocument;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string documentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _documents.Remove(documentId);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<SearchMatch>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var matches = _documents.Values
                .Select(document => document.Match(query))
                .Where(match => match is not null)
                .Select(match => match!)
                .ToArray();

            return Task.FromResult<IReadOnlyCollection<SearchMatch>>(matches);
        }
    }

    private static SearchDocumentKind GuessKind(string documentId)
    {
        if (documentId.StartsWith("bill:", StringComparison.OrdinalIgnoreCase))
        {
            return SearchDocumentKind.Bill;
        }

        if (documentId.StartsWith("act:", StringComparison.OrdinalIgnoreCase))
        {
            return SearchDocumentKind.Act;
        }

        if (documentId.StartsWith("process:", StringComparison.OrdinalIgnoreCase))
        {
            return SearchDocumentKind.Process;
        }

        if (documentId.StartsWith("profile:", StringComparison.OrdinalIgnoreCase))
        {
            return SearchDocumentKind.Profile;
        }

        return SearchDocumentKind.Alert;
    }

    private static IReadOnlyCollection<string> ExtractKeywords(string title, string content)
    {
        return $"{title} {content}"
            .Split([' ', '.', ',', ';', ':', '/', '\\', '-', '_', '\"', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
