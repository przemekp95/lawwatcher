using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LawWatcher.BuildingBlocks.Ports;

namespace LawWatcher.AiEnrichment.Infrastructure;

public sealed partial class OllamaLlmService(
    HttpClient httpClient,
    string model,
    string keepAlive) : ILlmService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly string _model = NormalizeRequired(model, nameof(model), "Configured Ollama model");
    private readonly string _keepAlive = NormalizeRequired(keepAlive, nameof(keepAlive), "Configured Ollama keep-alive");

    public async Task<LlmCompletion> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        var normalizedPrompt = NormalizeRequired(prompt, nameof(prompt), "Prompt");
        using var response = await _httpClient.PostAsJsonAsync(
            "/api/generate",
            new OllamaGenerateRequest(
                _model,
                normalizedPrompt,
                false,
                _keepAlive),
            SerializerOptions,
            cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Ollama generation request failed with status {(int)response.StatusCode}: {payload}");
        }

        var completion = JsonSerializer.Deserialize<OllamaGenerateResponse>(payload, SerializerOptions)
            ?? throw new InvalidOperationException("Ollama generation response could not be deserialized.");

        var content = NormalizeRequired(completion.Response, nameof(prompt), "Generated content");
        var completionModel = string.IsNullOrWhiteSpace(completion.Model) ? _model : completion.Model.Trim();
        var citations = UrlRegex()
            .Matches(content)
            .Select(match => TrimTrailingPunctuation(match.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LlmCompletion(
            completionModel,
            content,
            citations);
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    private static string TrimTrailingPunctuation(string value) =>
        value.TrimEnd('.', ',', ';', ':', ')', ']');

    private static string NormalizeRequired(string value, string paramName, string label)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"{label} cannot be empty.", paramName);
        }

        return normalized;
    }

    private sealed record OllamaGenerateRequest(
        string Model,
        string Prompt,
        bool Stream,
        [property: JsonPropertyName("keep_alive")]
        string KeepAlive);

    private sealed record OllamaGenerateResponse(
        string? Model,
        string Response);
}

public sealed record OllamaModelAvailability(
    bool ServerReachable,
    bool ModelAvailable);

public static class OllamaAvailabilityProbe
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task<OllamaModelAvailability> CheckAsync(
        HttpClient httpClient,
        string model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        var normalizedModel = model.Trim();
        if (normalizedModel.Length == 0)
        {
            throw new ArgumentException("Configured Ollama model cannot be empty.", nameof(model));
        }

        try
        {
            using var response = await httpClient.GetAsync("/api/tags", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new OllamaModelAvailability(false, false);
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            var tags = await JsonSerializer.DeserializeAsync<OllamaTagsResponse>(content, SerializerOptions, cancellationToken);
            var modelAvailable = tags?.Models?.Any(candidate =>
                ModelsMatch(normalizedModel, candidate.Name)) == true;

            return new OllamaModelAvailability(true, modelAvailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new OllamaModelAvailability(false, false);
        }
    }

    private sealed record OllamaTagsResponse(
        IReadOnlyCollection<OllamaTagModel>? Models);

    private sealed record OllamaTagModel(
        string? Name);

    private static bool ModelsMatch(string configuredModel, string? discoveredModel)
    {
        if (string.IsNullOrWhiteSpace(discoveredModel))
        {
            return false;
        }

        var normalizedConfiguredModel = NormalizeModelName(configuredModel);
        var normalizedDiscoveredModel = NormalizeModelName(discoveredModel);

        return normalizedConfiguredModel.Equals(normalizedDiscoveredModel, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeModelName(string model)
    {
        var normalized = model.Trim();
        if (normalized.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^":latest".Length];
        }

        return normalized;
    }
}
