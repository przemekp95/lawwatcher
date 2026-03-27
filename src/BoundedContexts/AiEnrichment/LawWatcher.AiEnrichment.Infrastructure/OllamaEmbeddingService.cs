using System.Net.Http.Json;
using System.Text.Json;
using LawWatcher.BuildingBlocks.Ports;

namespace LawWatcher.AiEnrichment.Infrastructure;

public sealed class OllamaEmbeddingService(
    HttpClient httpClient,
    string model) : IEmbeddingService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly string _model = NormalizeRequired(model, nameof(model), "Configured Ollama embedding model");

    public async Task<EmbeddingVector> GenerateAsync(string content, CancellationToken cancellationToken)
    {
        var normalizedContent = NormalizeRequired(content, nameof(content), "Embedding content");

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/embed",
            new OllamaEmbedRequest(_model, normalizedContent),
            SerializerOptions,
            cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Ollama embedding request failed with status {(int)response.StatusCode}: {payload}");
        }

        var completion = JsonSerializer.Deserialize<OllamaEmbedResponse>(payload, SerializerOptions)
            ?? throw new InvalidOperationException("Ollama embedding response could not be deserialized.");

        var vector = completion.Embeddings?.FirstOrDefault();
        if (vector is null || vector.Count == 0)
        {
            throw new InvalidOperationException("Ollama embedding response did not contain a vector.");
        }

        var completionModel = string.IsNullOrWhiteSpace(completion.Model) ? _model : completion.Model.Trim();
        return new EmbeddingVector(completionModel, vector.ToArray());
    }

    private static string NormalizeRequired(string value, string paramName, string label)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"{label} cannot be empty.", paramName);
        }

        return normalized;
    }

    private sealed record OllamaEmbedRequest(
        string Model,
        string Input);

    private sealed record OllamaEmbedResponse(
        string? Model,
        IReadOnlyCollection<IReadOnlyCollection<float>>? Embeddings);
}
