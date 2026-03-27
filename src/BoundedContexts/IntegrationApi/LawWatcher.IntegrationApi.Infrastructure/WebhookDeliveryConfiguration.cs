using LawWatcher.BuildingBlocks.Ports;
using Microsoft.Extensions.Logging;

namespace LawWatcher.IntegrationApi.Infrastructure;

public enum WebhookDispatcherBackend
{
    InMemory = 0,
    SignedHttp = 1
}

public sealed class WebhookDeliveryOptions
{
    public string Backend { get; init; } = "InMemory";

    public string SigningSecret { get; init; } = string.Empty;

    public int TimeoutSeconds { get; init; } = 10;
}

public static class WebhookDispatcherRuntimeResolver
{
    public static WebhookDispatcherBackend Select(WebhookDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Backend.Trim().ToLowerInvariant() switch
        {
            "signedhttp" or "signed-http" or "http" => WebhookDispatcherBackend.SignedHttp,
            _ => WebhookDispatcherBackend.InMemory
        };
    }
}

public static class WebhookDispatcherRuntimeFactory
{
    public static IWebhookDispatcher Create(WebhookDeliveryOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return WebhookDispatcherRuntimeResolver.Select(options) switch
        {
            WebhookDispatcherBackend.SignedHttp => new SignedHttpWebhookDispatcher(
                new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds))
                },
                options,
                loggerFactory?.CreateLogger<SignedHttpWebhookDispatcher>()
                    ?? LoggerFactory.Create(logging => { }).CreateLogger<SignedHttpWebhookDispatcher>()),
            _ => new InMemoryWebhookDispatcher()
        };
    }
}
