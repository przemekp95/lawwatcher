using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.BuildingBlocks.Ports;
using LawWatcher.SearchAndDiscovery.Application;
using LawWatcher.SearchAndDiscovery.Domain;

namespace LawWatcher.SearchAndDiscovery.Infrastructure;

public sealed class OpenSearchSearchDocumentIndex(
    HttpClient httpClient,
    IEmbeddingService embeddingService,
    OpenSearchOptions options) : ISearchDocumentIndex, ISearchIndexer
{
    private const int DefaultSearchSize = 25;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SourceFields = ["document_id", "title", "kind", "snippet", "keywords"];
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IEmbeddingService _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
    private readonly string _indexName = NormalizeIndexName(options.IndexName);
    private readonly int _defaultEmbeddingDimensions = Math.Max(1, options.EmbeddingDimensions);

    public async Task ReplaceAllAsync(IReadOnlyCollection<IndexedSearchDocument> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var indexedDocuments = new List<OpenSearchIndexedDocument>(documents.Count);
        foreach (var document in documents.OrderBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase))
        {
            indexedDocuments.Add(await BuildIndexedDocumentAsync(document, cancellationToken));
        }

        var embeddingDimensions = indexedDocuments.Count == 0
            ? _defaultEmbeddingDimensions
            : indexedDocuments[0].Embedding.Length;

        await RecreateIndexAsync(embeddingDimensions, cancellationToken);

        if (indexedDocuments.Count == 0)
        {
            return;
        }

        var payload = new StringBuilder();
        foreach (var document in indexedDocuments)
        {
            payload.AppendLine(JsonSerializer.Serialize(new
            {
                index = new
                {
                    _index = _indexName,
                    _id = document.Document.Id
                }
            }, SerializerOptions));
            payload.AppendLine(JsonSerializer.Serialize(ToSource(document), SerializerOptions));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{_indexName}/_bulk?refresh=true")
        {
            Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/x-ndjson")
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenSearch bulk indexing request failed with status {(int)response.StatusCode}: {body}");
        }

        var bulkResponse = JsonSerializer.Deserialize<OpenSearchBulkResponse>(body, SerializerOptions)
            ?? throw new InvalidOperationException("OpenSearch bulk response could not be deserialized.");
        if (bulkResponse.Errors)
        {
            throw new InvalidOperationException("OpenSearch bulk indexing reported item-level errors.");
        }
    }

    public async Task IndexAsync(string documentId, string title, string content, CancellationToken cancellationToken)
    {
        var indexedDocument = IndexedSearchDocument.Create(
            documentId,
            title,
            GuessKind(documentId),
            content,
            ExtractKeywords(title, content));

        var indexedPayload = await BuildIndexedDocumentAsync(indexedDocument, cancellationToken);
        await EnsureIndexAsync(indexedPayload.Embedding.Length, recreate: false, cancellationToken);

        using var response = await _httpClient.PutAsJsonAsync(
            $"/{_indexName}/_doc/{Uri.EscapeDataString(indexedDocument.Id)}?refresh=true",
            ToSource(indexedPayload),
            SerializerOptions,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenSearch document indexing request failed with status {(int)response.StatusCode}: {body}");
        }
    }

    public async Task RemoveAsync(string documentId, CancellationToken cancellationToken)
    {
        var normalizedDocumentId = NormalizeDocumentId(documentId);
        using var response = await _httpClient.DeleteAsync(
            $"/{_indexName}/_doc/{Uri.EscapeDataString(normalizedDocumentId)}?refresh=true",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenSearch delete request failed with status {(int)response.StatusCode}: {body}");
        }
    }

    public async Task<IReadOnlyCollection<SearchMatch>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var lexicalHits = await SearchLexicalAsync(query.Trim(), cancellationToken);
        var vectorHits = await SearchVectorAsync(query.Trim(), cancellationToken);

        var candidates = lexicalHits.Count != 0
            ? lexicalHits.Values
                .Select(lexical => vectorHits.TryGetValue(lexical.Document.Id, out var vector)
                    ? lexical with { VectorScore = vector.VectorScore }
                    : lexical)
            : vectorHits.Values;

        return candidates
            .Select(candidate => new SearchMatch(
                candidate.Document,
                IndexedSearchDocument.BaseScoreForKind(candidate.Document.Kind)
                + (int)Math.Round(candidate.LexicalScore * 100D, MidpointRounding.AwayFromZero)
                + (int)Math.Round(candidate.VectorScore * 100D, MidpointRounding.AwayFromZero)))
            .ToArray();
    }

    private async Task<Dictionary<string, OpenSearchScoredDocument>> SearchLexicalAsync(string query, CancellationToken cancellationToken)
    {
        var body = new
        {
            size = DefaultSearchSize,
            _source = SourceFields,
            query = new
            {
                multi_match = new
                {
                    query,
                    fields = new[] { "title^3", "snippet^2", "keywords_text^2" },
                    type = "best_fields",
                    @operator = "or"
                }
            }
        };

        var response = await PostSearchAsync(body, cancellationToken);
        return response
            .Select(hit => new OpenSearchScoredDocument(
                hit.Document,
                hit.Score,
                VectorScore: 0D))
            .ToDictionary(candidate => candidate.Document.Id, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, OpenSearchScoredDocument>> SearchVectorAsync(string query, CancellationToken cancellationToken)
    {
        var queryEmbedding = await _embeddingService.GenerateAsync(query, cancellationToken);
        if (queryEmbedding.Values.Count == 0)
        {
            return new Dictionary<string, OpenSearchScoredDocument>(StringComparer.OrdinalIgnoreCase);
        }

        var body = new Dictionary<string, object?>
        {
            ["size"] = DefaultSearchSize,
            ["_source"] = SourceFields,
            ["query"] = new Dictionary<string, object?>
            {
                ["knn"] = new Dictionary<string, object?>
                {
                    ["embedding"] = new Dictionary<string, object?>
                    {
                        ["vector"] = queryEmbedding.Values.ToArray(),
                        ["k"] = DefaultSearchSize
                    }
                }
            }
        };

        var response = await PostSearchAsync(body, cancellationToken);
        return response
            .Select(hit => new OpenSearchScoredDocument(
                hit.Document,
                LexicalScore: 0D,
                hit.Score))
            .ToDictionary(candidate => candidate.Document.Id, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyCollection<OpenSearchSearchHit>> PostSearchAsync(object payload, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            $"/{_indexName}/_search",
            payload,
            SerializerOptions,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenSearch search request failed with status {(int)response.StatusCode}: {body}");
        }

        var searchResponse = JsonSerializer.Deserialize<OpenSearchSearchResponse>(body, SerializerOptions)
            ?? throw new InvalidOperationException("OpenSearch search response could not be deserialized.");

        return searchResponse.Hits?.Hits?
            .Where(hit => hit.Source is not null)
            .Select(ToSearchHit)
            .ToArray()
            ?? [];
    }

    private async Task<OpenSearchIndexedDocument> BuildIndexedDocumentAsync(IndexedSearchDocument document, CancellationToken cancellationToken)
    {
        var embedding = await _embeddingService.GenerateAsync(BuildEmbeddingContent(document), cancellationToken);
        var values = embedding.Values.ToArray();
        if (values.Length == 0)
        {
            throw new InvalidOperationException("Embedding service returned an empty vector.");
        }

        return new OpenSearchIndexedDocument(document, values);
    }

    private async Task RecreateIndexAsync(int embeddingDimensions, CancellationToken cancellationToken)
    {
        await DeleteIndexIfExistsAsync(cancellationToken);
        await EnsureIndexAsync(embeddingDimensions, recreate: true, cancellationToken);
    }

    private async Task DeleteIndexIfExistsAsync(CancellationToken cancellationToken)
    {
        using var deleteResponse = await _httpClient.DeleteAsync($"/{_indexName}", cancellationToken);
        if (deleteResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        var deleteBody = await deleteResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!deleteResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenSearch index delete request failed with status {(int)deleteResponse.StatusCode}: {deleteBody}");
        }
    }

    private async Task EnsureIndexAsync(int embeddingDimensions, bool recreate, CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            settings = new
            {
                index = new
                {
                    knn = true,
                    number_of_shards = 1,
                    number_of_replicas = 0
                }
            },
            mappings = new
            {
                properties = new
                {
                    document_id = new { type = "keyword" },
                    title = new { type = "text" },
                    kind = new { type = "keyword" },
                    snippet = new { type = "text" },
                    keywords = new { type = "keyword" },
                    keywords_text = new { type = "text" },
                    embedding = new
                    {
                        type = "knn_vector",
                        dimension = embeddingDimensions,
                        method = new
                        {
                            name = "hnsw",
                            space_type = "cosinesimil",
                            engine = "lucene"
                        }
                    }
                }
            }
        };

        using var response = await _httpClient.PutAsJsonAsync(
            $"/{_indexName}",
            requestBody,
            SerializerOptions,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!recreate && body.Contains("resource_already_exists_exception", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"OpenSearch index create request failed with status {(int)response.StatusCode}: {body}");
    }

    private static string BuildEmbeddingContent(IndexedSearchDocument document) =>
        string.Join(
            Environment.NewLine,
            new[]
            {
                document.Title,
                document.Snippet,
                string.Join(' ', document.Keywords)
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static OpenSearchDocumentSource ToSource(OpenSearchIndexedDocument document) =>
        new(
            document.Document.Id,
            document.Document.Title,
            document.Document.Kind.ToString(),
            document.Document.Snippet,
            document.Document.Keywords.ToArray(),
            string.Join(' ', document.Document.Keywords),
            document.Embedding);

    private static OpenSearchSearchHit ToSearchHit(OpenSearchRawHit hit)
    {
        var source = hit.Source ?? throw new InvalidOperationException("OpenSearch search hit did not include a _source payload.");
        if (!Enum.TryParse<SearchDocumentKind>(source.Kind, ignoreCase: true, out var parsedKind))
        {
            throw new InvalidOperationException($"Unsupported search document kind '{source.Kind}'.");
        }

        return new OpenSearchSearchHit(
            IndexedSearchDocument.Create(
                source.DocumentId,
                source.Title,
                parsedKind,
                source.Snippet,
                source.Keywords ?? []),
            hit.Score ?? 0D);
    }

    private static string NormalizeIndexName(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("OpenSearch index name cannot be empty.", nameof(value));
        }

        return normalized.ToLowerInvariant();
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

    private sealed record OpenSearchIndexedDocument(
        IndexedSearchDocument Document,
        float[] Embedding);

    private sealed record OpenSearchScoredDocument(
        IndexedSearchDocument Document,
        double LexicalScore,
        double VectorScore);

    private sealed record OpenSearchDocumentSource(
        [property: JsonPropertyName("document_id")]
        string DocumentId,
        [property: JsonPropertyName("title")]
        string Title,
        [property: JsonPropertyName("kind")]
        string Kind,
        [property: JsonPropertyName("snippet")]
        string Snippet,
        [property: JsonPropertyName("keywords")]
        string[] Keywords,
        [property: JsonPropertyName("keywords_text")]
        string KeywordsText,
        [property: JsonPropertyName("embedding")]
        float[] Embedding);

    private sealed record OpenSearchBulkResponse(
        [property: JsonPropertyName("errors")]
        bool Errors);

    private sealed record OpenSearchSearchResponse(
        [property: JsonPropertyName("hits")]
        OpenSearchHitsContainer? Hits);

    private sealed record OpenSearchHitsContainer(
        [property: JsonPropertyName("hits")]
        IReadOnlyCollection<OpenSearchRawHit>? Hits);

    private sealed record OpenSearchRawHit(
        [property: JsonPropertyName("_score")]
        double? Score,
        [property: JsonPropertyName("_source")]
        OpenSearchSearchSource? Source);

    private sealed record OpenSearchSearchSource(
        [property: JsonPropertyName("document_id")]
        string DocumentId,
        [property: JsonPropertyName("title")]
        string Title,
        [property: JsonPropertyName("kind")]
        string Kind,
        [property: JsonPropertyName("snippet")]
        string Snippet,
        [property: JsonPropertyName("keywords")]
        string[]? Keywords);

    private sealed record OpenSearchSearchHit(
        IndexedSearchDocument Document,
        double Score);
}

public static class OpenSearchAvailabilityProbe
{
    public static async Task<bool> CheckAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        try
        {
            using var response = await httpClient.GetAsync(
                "/_cluster/health?wait_for_status=yellow&timeout=5s",
                cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
