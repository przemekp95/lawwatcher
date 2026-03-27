using LawWatcher.AiEnrichment.Infrastructure;
using LawWatcher.BuildingBlocks.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace LawWatcher.Worker.Ai;

public sealed class OllamaReadinessHealthCheck(
    IOptionsMonitor<LawWatcherRuntimeOptions> runtimeOptionsMonitor,
    IOptionsMonitor<LocalLlmWorkerOptions> localLlmWorkerOptionsMonitor,
    IOptionsMonitor<OllamaOptions> ollamaOptionsMonitor) : IHealthCheck
{
    private readonly IOptionsMonitor<LawWatcherRuntimeOptions> _runtimeOptionsMonitor = runtimeOptionsMonitor;
    private readonly IOptionsMonitor<LocalLlmWorkerOptions> _localLlmWorkerOptionsMonitor = localLlmWorkerOptionsMonitor;
    private readonly IOptionsMonitor<OllamaOptions> _ollamaOptionsMonitor = ollamaOptionsMonitor;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_runtimeOptionsMonitor.CurrentValue.Capabilities.Ai)
        {
            return HealthCheckResult.Healthy("AI capability is disabled for this runtime.");
        }

        try
        {
            var ollamaOptions = _ollamaOptionsMonitor.CurrentValue;
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(ollamaOptions.BaseUrl, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(Math.Max(5, ollamaOptions.RequestTimeoutSeconds))
            };

            var availability = await OllamaAvailabilityProbe.CheckAsync(
                httpClient,
                _localLlmWorkerOptionsMonitor.CurrentValue.DefaultModel,
                cancellationToken);

            if (!availability.ServerReachable)
            {
                return new HealthCheckResult(
                    context.Registration.FailureStatus,
                    "Ollama server is not reachable.");
            }

            if (!availability.ModelAvailable)
            {
                return new HealthCheckResult(
                    context.Registration.FailureStatus,
                    "Configured Ollama model is not available.");
            }

            return HealthCheckResult.Healthy("Configured Ollama server and model are available.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "Ollama readiness probe failed.",
                exception);
        }
    }
}
