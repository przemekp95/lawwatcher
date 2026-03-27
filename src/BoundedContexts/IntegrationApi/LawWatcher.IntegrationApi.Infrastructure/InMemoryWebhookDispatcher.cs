using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using LawWatcher.BuildingBlocks.Ports;
using Microsoft.Extensions.Logging;

namespace LawWatcher.IntegrationApi.Infrastructure;

public sealed record WebhookDispatchRecord(
    string CallbackUrl,
    string EventType,
    string Payload,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset DispatchedAtUtc);

public sealed class InMemoryWebhookDispatcher : IWebhookDispatcher
{
    private readonly List<WebhookDispatchRecord> _dispatches = [];
    private readonly Lock _gate = new();

    public Task DispatchAsync(WebhookDispatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var record = new WebhookDispatchRecord(
            request.CallbackUrl,
            request.EventType,
            request.Payload,
            new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow);

        lock (_gate)
        {
            _dispatches.Add(record);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WebhookDispatchRecord>> GetDispatchesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<WebhookDispatchRecord>>(_dispatches.ToArray());
        }
    }
}

public sealed class SignedHttpWebhookDispatcher(
    HttpClient httpClient,
    WebhookDeliveryOptions options,
    ILogger<SignedHttpWebhookDispatcher> logger) : IWebhookDispatcher
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly WebhookDeliveryOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<SignedHttpWebhookDispatcher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task DispatchAsync(WebhookDispatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(_options.SigningSecret))
        {
            throw new InvalidOperationException("Signed HTTP webhook dispatcher requires a non-empty signing secret.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, request.CallbackUrl)
        {
            Content = new StringContent(request.Payload, Encoding.UTF8, "application/json")
        };

        foreach (var header in CreateHeaders(request))
        {
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation(
            "signed webhook dispatched. flow=signed-webhook eventType={EventType} callbackUrl={CallbackUrl} statusCode={StatusCode}",
            request.EventType,
            request.CallbackUrl,
            (int)response.StatusCode);
    }

    internal IReadOnlyDictionary<string, string> CreateHeaders(WebhookDispatchRequest request)
    {
        var headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase)
        {
            ["X-LawWatcher-Signature"] = ComputeSignature(request.Payload, _options.SigningSecret)
        };

        if (!headers.ContainsKey("X-LawWatcher-Event-Type"))
        {
            headers["X-LawWatcher-Event-Type"] = request.EventType;
        }

        return headers;
    }

    internal static string ComputeSignature(string payload, string signingSecret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
        return $"sha256={Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)))}";
    }
}
