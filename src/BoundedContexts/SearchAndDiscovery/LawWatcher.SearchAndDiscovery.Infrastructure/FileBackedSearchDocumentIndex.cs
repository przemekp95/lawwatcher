using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.BuildingBlocks.Ports;
using LawWatcher.SearchAndDiscovery.Application;
using LawWatcher.SearchAndDiscovery.Domain;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace LawWatcher.SearchAndDiscovery.Infrastructure;

public sealed class FileBackedSearchDocumentIndex(string rootPath) : ISearchDocumentIndex, ISearchIndexer
{
    private readonly string _rootPath = rootPath;
    private readonly string _projectionPath = Path.Combine(rootPath, "documents.json");

    public Task ReplaceAllAsync(IReadOnlyCollection<IndexedSearchDocument> documents, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var projection = new SearchDocumentProjection(
                    documents
                        .Select(SearchDocumentRecord.FromDomain)
                        .OrderBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                        .ToArray());

                await JsonFilePersistence.SaveAsync(_projectionPath, projection, ct);
            },
            cancellationToken);
    }

    public Task IndexAsync(string documentId, string title, string content, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var projection = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new SearchDocumentProjection([]),
                    ct);

                var documents = projection.Documents.ToDictionary(record => record.Id, StringComparer.OrdinalIgnoreCase);
                var indexedDocument = IndexedSearchDocument.Create(
                    documentId,
                    title,
                    GuessKind(documentId),
                    content,
                    ExtractKeywords(title, content));

                documents[indexedDocument.Id] = SearchDocumentRecord.FromDomain(indexedDocument);

                await JsonFilePersistence.SaveAsync(
                    _projectionPath,
                    new SearchDocumentProjection(
                        documents.Values
                            .OrderBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                            .ToArray()),
                    ct);
            },
            cancellationToken);
    }

    public Task RemoveAsync(string documentId, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var projection = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new SearchDocumentProjection([]),
                    ct);

                var documents = projection.Documents
                    .Where(record => !string.Equals(record.Id, documentId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                await JsonFilePersistence.SaveAsync(
                    _projectionPath,
                    new SearchDocumentProjection(documents),
                    ct);
            },
            cancellationToken);
    }

    public Task<IReadOnlyCollection<SearchMatch>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var projection = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new SearchDocumentProjection([]),
                    ct);

                var matches = projection.Documents
                    .Select(record => record.ToDomain())
                    .Select(document => document.Match(query))
                    .Where(match => match is not null)
                    .Select(match => match!)
                    .ToArray();

                return (IReadOnlyCollection<SearchMatch>)matches;
            },
            cancellationToken);
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

    private sealed record SearchDocumentProjection(SearchDocumentRecord[] Documents);

    private sealed record SearchDocumentRecord(
        string Id,
        string Title,
        string Kind,
        string Snippet,
        string[] Keywords)
    {
        public static SearchDocumentRecord FromDomain(IndexedSearchDocument document) => new(
            document.Id,
            document.Title,
            document.Kind.ToString(),
            document.Snippet,
            document.Keywords.ToArray());

        public IndexedSearchDocument ToDomain()
        {
            if (!Enum.TryParse<SearchDocumentKind>(Kind, ignoreCase: true, out var parsedKind))
            {
                throw new InvalidOperationException($"Unsupported search document kind '{Kind}'.");
            }

            return IndexedSearchDocument.Create(Id, Title, parsedKind, Snippet, Keywords);
        }
    }
}

public sealed class SqlServerSearchDocumentIndex(
    string connectionString,
    string schema = "lawwatcher",
    bool useSqlFullText = false) : ISearchDocumentIndex, ISearchIndexer
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);
    private readonly bool _useSqlFullText = useSqlFullText;

    public async Task ReplaceAllAsync(IReadOnlyCollection<IndexedSearchDocument> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var orderedDocuments = documents
            .OrderBy(document => document.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var indexedAtUtc = DateTimeOffset.UtcNow;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var deleteSql = $"""DELETE FROM [{_schema}].[search_documents];""";
        await using (var deleteCommand = new SqlCommand(deleteSql, connection, transaction))
        {
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var document in orderedDocuments)
        {
            await UpsertAsync(document, indexedAtUtc, connection, transaction, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task IndexAsync(string documentId, string title, string content, CancellationToken cancellationToken)
    {
        var indexedDocument = IndexedSearchDocument.Create(
            documentId,
            title,
            GuessKind(documentId),
            content,
            ExtractKeywords(title, content));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await UpsertAsync(indexedDocument, DateTimeOffset.UtcNow, connection, transaction: null, cancellationToken);
    }

    public async Task RemoveAsync(string documentId, CancellationToken cancellationToken)
    {
        var normalizedDocumentId = NormalizeDocumentId(documentId);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            DELETE FROM [{_schema}].[search_documents]
            WHERE [document_id] = @documentId;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@documentId", normalizedDocumentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<SearchMatch>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (_useSqlFullText)
        {
            var fullTextMatches = await TrySearchUsingFullTextAsync(query, cancellationToken);
            if (fullTextMatches is not null)
            {
                return fullTextMatches;
            }
        }

        return await SearchUsingProjectionIndexAsync(query, cancellationToken);
    }

    private async Task<IReadOnlyCollection<SearchMatch>?> TrySearchUsingFullTextAsync(string query, CancellationToken cancellationToken)
    {
        var searchCondition = SqlServerFullTextSearchConditionBuilder.Build(query);
        if (searchCondition is null)
        {
            return [];
        }

        var matches = new List<SearchMatch>();

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var sql = $"""
                SELECT
                    [documents].[document_id],
                    [documents].[title],
                    [documents].[kind],
                    [documents].[snippet],
                    [documents].[keywords_json],
                    [fulltext].[RANK]
                FROM CONTAINSTABLE(
                    [{_schema}].[search_documents],
                    ([title], [snippet], [keywords_text]),
                    @searchCondition) AS [fulltext]
                INNER JOIN [{_schema}].[search_documents] AS [documents]
                    ON [documents].[document_id] = [fulltext].[KEY];
                """;

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@searchCondition", searchCondition);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var document = ToDomain(reader);
                var fullTextRank = reader.GetInt32(5);
                matches.Add(new SearchMatch(
                    document,
                    IndexedSearchDocument.BaseScoreForKind(document.Kind) + fullTextRank));
            }

            return matches;
        }
        catch (SqlException exception) when (IsFullTextUnavailable(exception))
        {
            return null;
        }
    }

    private async Task<IReadOnlyCollection<SearchMatch>> SearchUsingProjectionIndexAsync(string query, CancellationToken cancellationToken)
    {
        var documents = new List<IndexedSearchDocument>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [document_id],
                [title],
                [kind],
                [snippet],
                [keywords_json]
            FROM [{_schema}].[search_documents];
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(ToDomain(reader));
        }

        return documents
            .Select(document => document.Match(query))
            .Where(match => match is not null)
            .Select(match => match!)
            .ToArray();
    }

    private async Task UpsertAsync(
        IndexedSearchDocument document,
        DateTimeOffset indexedAtUtc,
        SqlConnection connection,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[search_documents]
            SET
                [title] = @title,
                [kind] = @kind,
                [snippet] = @snippet,
                [keywords_json] = @keywordsJson,
                [keywords_text] = @keywordsText,
                [indexed_at_utc] = @indexedAtUtc
            WHERE [document_id] = @documentId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[search_documents]
                (
                    [document_id],
                [title],
                [kind],
                [snippet],
                [keywords_json],
                [keywords_text],
                [indexed_at_utc]
            )
            VALUES
            (
                @documentId,
                @title,
                @kind,
                @snippet,
                @keywordsJson,
                @keywordsText,
                @indexedAtUtc
            );
            END
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@documentId", document.Id);
        command.Parameters.AddWithValue("@title", document.Title);
        command.Parameters.AddWithValue("@kind", document.Kind.ToString());
        command.Parameters.AddWithValue("@snippet", document.Snippet);
        command.Parameters.AddWithValue("@keywordsJson", JsonSerializer.Serialize(document.Keywords));
        command.Parameters.AddWithValue("@keywordsText", string.Join(' ', document.Keywords));
        command.Parameters.AddWithValue("@indexedAtUtc", indexedAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IndexedSearchDocument ToDomain(SqlDataReader reader)
    {
        var kindValue = reader.GetString(2);
        if (!Enum.TryParse<SearchDocumentKind>(kindValue, ignoreCase: true, out var parsedKind))
        {
            throw new InvalidOperationException($"Unsupported search document kind '{kindValue}'.");
        }

        var keywordsJson = reader.GetString(4);
        var keywords = JsonSerializer.Deserialize<string[]>(keywordsJson) ?? [];
        return IndexedSearchDocument.Create(
            reader.GetString(0),
            reader.GetString(1),
            parsedKind,
            reader.GetString(3),
            keywords);
    }

    private static string NormalizeDocumentId(string documentId)
    {
        var normalized = documentId.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Document id cannot be empty.", nameof(documentId));
        }

        return normalized;
    }

    private static string ValidateSchema(string schema)
    {
        var normalized = schema.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Schema cannot be empty.", nameof(schema));
        }

        return normalized;
    }

    private static bool IsFullTextUnavailable(SqlException exception) =>
        exception.Message.Contains("full-text", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("fulltext", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("CONTAINSTABLE", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("CONTAINS", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("FREETEXT", StringComparison.OrdinalIgnoreCase);

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
