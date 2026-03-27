using System.Net.Sockets;
using System.Text.Json;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LawWatcher.BuildingBlocks.Configuration;

namespace LawWatcher.BuildingBlocks.Health;

public static class LawWatcherHealthTags
{
    public const string Live = "live";

    public const string Ready = "ready";
}

public sealed class HostReadinessState
{
    private int _isReady;

    public bool IsReady => Volatile.Read(ref _isReady) == 1;

    public void MarkReady()
    {
        Interlocked.Exchange(ref _isReady, 1);
    }
}

public sealed class HostReadinessHealthCheck(HostReadinessState readinessState) : IHealthCheck
{
    private readonly HostReadinessState _readinessState = readinessState;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(
            _readinessState.IsReady
                ? HealthCheckResult.Healthy("Host startup completed.")
                : new HealthCheckResult(context.Registration.FailureStatus, "Host startup is still in progress."));
    }
}

public sealed class SqlServerConnectionHealthCheck(string connectionString) : IHealthCheck
{
    private readonly string _connectionString = connectionString;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            _ = await command.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy("SQL Server connection succeeded.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "SQL Server connection failed.",
                exception);
        }
    }
}

public sealed class RabbitMqConnectionHealthCheck(string connectionString) : IHealthCheck
{
    private readonly string _connectionString = connectionString;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = new Uri(_connectionString, UriKind.Absolute);
            var port = uri.Port > 0 ? uri.Port : 5672;

            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, port, cancellationToken);

            return HealthCheckResult.Healthy("RabbitMQ TCP connection succeeded.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "RabbitMQ TCP connection failed.",
                exception);
        }
    }
}

public sealed class OpenSearchConnectionHealthCheck(string baseUrl) : IHealthCheck
{
    private readonly string _baseUrl = baseUrl;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(_baseUrl, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(10)
            };

            using var response = await httpClient.GetAsync(
                "/_cluster/health?wait_for_status=yellow&timeout=5s",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new HealthCheckResult(
                    context.Registration.FailureStatus,
                    $"OpenSearch returned status {(int)response.StatusCode}.");
            }

            return HealthCheckResult.Healthy("OpenSearch HTTP connection succeeded.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "OpenSearch HTTP connection failed.",
                exception);
        }
    }
}

public static class LawWatcherHealthResponseWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(
        HttpContext context,
        HealthCheckService healthCheckService,
        string tag,
        CancellationToken cancellationToken)
    {
        var report = await healthCheckService.CheckHealthAsync(
            registration => registration.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase),
            cancellationToken);

        context.Response.StatusCode = report.Status == HealthStatus.Healthy
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            entries = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString().ToLowerInvariant(),
                    description = entry.Value.Description ?? string.Empty,
                    durationMs = entry.Value.Duration.TotalMilliseconds
                },
                StringComparer.OrdinalIgnoreCase)
        };

        await JsonSerializer.SerializeAsync(context.Response.Body, payload, SerializerOptions, cancellationToken);
    }
}

public sealed class WorkerHealthServerHostedService(
    IOptionsMonitor<HostHealthOptions> optionsMonitor,
    HealthCheckService healthCheckService,
    ILogger<WorkerHealthServerHostedService> logger) : IHostedService
{
    private readonly IOptionsMonitor<HostHealthOptions> _optionsMonitor = optionsMonitor;
    private readonly HealthCheckService _healthCheckService = healthCheckService;
    private readonly ILogger<WorkerHealthServerHostedService> _logger = logger;
    private IHost? _healthHost;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.Urls))
        {
            _logger.LogDebug("Auxiliary worker health server is disabled because LawWatcher:Health:Urls is not configured.");
            return;
        }

        var livePath = NormalizePath(options.LivePath, "/health/live");
        var readyPath = NormalizePath(options.ReadyPath, "/health/ready");

        _healthHost = Host.CreateDefaultBuilder(Array.Empty<string>())
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseKestrel();
                webBuilder.UseUrls(options.Urls);
                webBuilder.Configure(app =>
                {
                    app.Run(async context =>
                    {
                        if (!HttpMethods.IsGet(context.Request.Method))
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            return;
                        }

                        var path = context.Request.Path.Value ?? "/";
                        if (string.Equals(path, livePath, StringComparison.OrdinalIgnoreCase))
                        {
                            await LawWatcherHealthResponseWriter.WriteAsync(
                                context,
                                _healthCheckService,
                                LawWatcherHealthTags.Live,
                                context.RequestAborted);
                            return;
                        }

                        if (string.Equals(path, readyPath, StringComparison.OrdinalIgnoreCase))
                        {
                            await LawWatcherHealthResponseWriter.WriteAsync(
                                context,
                                _healthCheckService,
                                LawWatcherHealthTags.Ready,
                                context.RequestAborted);
                            return;
                        }

                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                    });
                });
            })
            .Build();

        await _healthHost.StartAsync(cancellationToken);
        _logger.LogInformation("Worker health endpoints are listening on {Urls}.", options.Urls);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_healthHost is null)
        {
            return;
        }

        await _healthHost.StopAsync(cancellationToken);
        _healthHost.Dispose();
        _healthHost = null;
    }

    private static string NormalizePath(string path, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(path)
            ? fallback
            : path.Trim();

        return candidate.StartsWith("/", StringComparison.Ordinal)
            ? candidate
            : "/" + candidate;
    }
}
