using LawWatcher.AiEnrichment.Application;
using LawWatcher.AiEnrichment.Infrastructure;
using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.BuildingBlocks.Health;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.BuildingBlocks.Ports;
using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegalCorpus.Infrastructure;
using MassTransit;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Data.SqlClient;
using System.IO;

namespace LawWatcher.Worker.Ai;

public static class WorkerAiHostApplication
{
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        var stateStorageOptions = builder.Configuration.GetSection("LawWatcher:Storage").Get<StateStorageOptions>() ?? new StateStorageOptions();
        var stateRoot = StateStoragePathResolver.ResolveRoot(stateStorageOptions, builder.Environment.ContentRootPath);
        var statePaths = LawWatcherStatePaths.ForRoot(stateRoot);
        var sqlServerStorageConnectionString = ResolveSqlServerStorageConnectionString(stateStorageOptions, builder.Configuration);
        var rabbitMqConnectionString = ResolveRabbitMqConnectionString(builder.Configuration);
        var readinessState = new HostReadinessState();
        Directory.CreateDirectory(statePaths.AiTasksRoot);
        Directory.CreateDirectory(statePaths.ActsRoot);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        if (builder.Environment.IsDevelopment())
        {
            builder.Logging.AddDebug();
        }

        builder.Services.Configure<LawWatcherRuntimeOptions>(builder.Configuration.GetSection("LawWatcher:Runtime"));
        builder.Services.Configure<StateStorageOptions>(builder.Configuration.GetSection("LawWatcher:Storage"));
        builder.Services.Configure<HostHealthOptions>(builder.Configuration.GetSection("LawWatcher:Health"));
        builder.Services.Configure<ObjectStorageOptions>(builder.Configuration.GetSection("Storage"));
        builder.Services.Configure<LocalLlmWorkerOptions>(builder.Configuration.GetSection("LawWatcher:LocalLlmWorker"));
        builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("AI:Ollama"));
        builder.Services.AddSingleton(readinessState);
        builder.Services.AddSingleton<HostReadinessHealthCheck>();
        builder.Services.AddSingleton<OllamaReadinessHealthCheck>();
        var healthChecks = builder.Services.AddHealthChecks();
        healthChecks.AddCheck("self", () => HealthCheckResult.Healthy("Worker.Ai host is running."), tags: [LawWatcherHealthTags.Live]);
        healthChecks.AddCheck<HostReadinessHealthCheck>("startup", tags: [LawWatcherHealthTags.Ready]);
        healthChecks.AddCheck<OllamaReadinessHealthCheck>("ollama", tags: [LawWatcherHealthTags.Ready]);
        if (sqlServerStorageConnectionString is not null)
        {
            healthChecks.AddCheck("sqlserver", new SqlServerConnectionHealthCheck(sqlServerStorageConnectionString), tags: [LawWatcherHealthTags.Ready]);
        }

        if (rabbitMqConnectionString is not null)
        {
            healthChecks.AddCheck("rabbitmq", new RabbitMqConnectionHealthCheck(rabbitMqConnectionString), tags: [LawWatcherHealthTags.Ready]);
        }

        if (rabbitMqConnectionString is not null)
        {
            builder.Services.AddMassTransit(services =>
            {
                services.SetKebabCaseEndpointNameFormatter();
                services.AddConsumer<AiEnrichmentRequestedConsumer>();
                services.UsingRabbitMq((context, configuration) =>
                {
                    ConfigureRabbitMqHost(configuration, rabbitMqConnectionString);
                    ConfigureBrokerConsumerResiliency(configuration);
                    configuration.ConfigureEndpoints(context);
                });
            });
            builder.Services.AddSingleton<AiEnrichmentRequestedMessageHandler>();
        }

        var objectStorageOptions = builder.Configuration.GetSection("Storage").Get<ObjectStorageOptions>() ?? new ObjectStorageOptions();
        builder.Services.AddSingleton<IDocumentStore>(_ =>
        {
            return DocumentStoreRuntimeResolver.Select(objectStorageOptions) switch
            {
                DocumentStoreBackend.S3Compatible => new S3CompatibleDocumentStore(objectStorageOptions.Minio),
                _ => new LocalFileDocumentStore(DocumentStoreRuntimeResolver.ResolveLocalDocumentsRoot(
                    objectStorageOptions,
                    builder.Environment.ContentRootPath))
            };
        });
        builder.Services.AddSingleton<IOcrService, PlainTextOcrService>();

        if (sqlServerStorageConnectionString is not null)
        {
            builder.Services.AddSingleton<IEventStore>(_ => new SqlServerEventStore(sqlServerStorageConnectionString));
            builder.Services.AddSingleton<IOutboxMessageStore>(_ => new SqlServerOutboxStore(sqlServerStorageConnectionString));
            builder.Services.AddSingleton<IInboxStore>(_ => new SqlServerInboxStore(sqlServerStorageConnectionString));
            builder.Services.AddSingleton<SqlServerAiEnrichmentTaskProjectionStore>(_ => new SqlServerAiEnrichmentTaskProjectionStore(sqlServerStorageConnectionString));
            builder.Services.AddSingleton<IAiEnrichmentTaskProjection>(serviceProvider => serviceProvider.GetRequiredService<SqlServerAiEnrichmentTaskProjectionStore>());
            builder.Services.AddSingleton<IAiEnrichmentTaskReadRepository>(serviceProvider => serviceProvider.GetRequiredService<SqlServerAiEnrichmentTaskProjectionStore>());
            builder.Services.AddSingleton<IAiEnrichmentTaskRepository>(serviceProvider => new SqlServerAiEnrichmentTaskRepository(
                serviceProvider.GetRequiredService<IEventStore>(),
                sqlServerStorageConnectionString));
        }
        else
        {
            builder.Services.AddSingleton<IAiEnrichmentTaskProjection>(_ => new FileBackedAiEnrichmentTaskProjectionStore(statePaths.AiTasksRoot));
            builder.Services.AddSingleton<IAiEnrichmentTaskReadRepository>(serviceProvider => serviceProvider.GetRequiredService<IAiEnrichmentTaskProjection>() as IAiEnrichmentTaskReadRepository
                ?? throw new InvalidOperationException("AI enrichment projection store must implement the read repository."));
            builder.Services.AddSingleton<IAiEnrichmentTaskRepository>(_ => new FileBackedAiEnrichmentTaskRepository(statePaths.AiTasksRoot));
        }

        if (sqlServerStorageConnectionString is not null)
        {
            builder.Services.AddSingleton<IPublishedActRepository>(serviceProvider => new SqlServerPublishedActRepository(
                serviceProvider.GetRequiredService<IEventStore>(),
                sqlServerStorageConnectionString));
        }
        else
        {
            builder.Services.AddSingleton<IPublishedActRepository>(_ => new FileBackedPublishedActRepository(statePaths.ActsRoot));
        }

        builder.Services.AddSingleton<ILlmService>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<LocalLlmWorkerOptions>>().CurrentValue;
            var ollamaOptions = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OllamaOptions>>().CurrentValue;

            return new OllamaLlmService(
                new HttpClient
                {
                    BaseAddress = new Uri(ollamaOptions.BaseUrl, UriKind.Absolute),
                    Timeout = TimeSpan.FromSeconds(Math.Max(5, ollamaOptions.RequestTimeoutSeconds))
                },
                options.DefaultModel,
                $"{Math.Max(1, options.UnloadAfterIdleSeconds)}s");
        });
        builder.Services.AddSingleton<IAiPromptAugmentor, PublishedActAiPromptAugmentor>();
        builder.Services.AddSingleton<AiEnrichmentExecutionService>();
        builder.Services.AddSingleton<AiEnrichmentQueueProcessor>();
        builder.Services.AddHostedService<WorkerHealthServerHostedService>();
        builder.Services.AddHostedService<Worker>();

        var host = builder.Build();
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(readinessState.MarkReady);
        return host;
    }

    private static string? ResolveSqlServerStorageConnectionString(StateStorageOptions options, IConfiguration configuration)
    {
        if (!string.Equals(options.Provider, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var connectionString = configuration.GetConnectionString(options.SqlServerConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Storage provider 'sqlserver' requires connection string '{options.SqlServerConnectionStringName}'.");
        }

        return connectionString;
    }

    private static string? ResolveRabbitMqConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("RabbitMq");
        return string.IsNullOrWhiteSpace(connectionString)
            ? null
            : connectionString;
    }

    private static void ConfigureRabbitMqHost(IRabbitMqBusFactoryConfigurator configuration, string connectionString)
    {
        var uri = new Uri(connectionString, UriKind.Absolute);
        var userInfo = uri.UserInfo.Split(':', 2, StringSplitOptions.None);
        var username = userInfo.Length >= 1 && userInfo[0].Length != 0
            ? Uri.UnescapeDataString(userInfo[0])
            : "guest";
        var password = userInfo.Length == 2
            ? Uri.UnescapeDataString(userInfo[1])
            : "guest";
        var virtualHost = string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/"
            ? "/"
            : Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));

        configuration.Host(uri.Host, uri.Port > 0 ? (ushort)uri.Port : (ushort)5672, virtualHost, host =>
        {
            host.Username(username);
            host.Password(password);
        });
    }

    private static void ConfigureBrokerConsumerResiliency(IRabbitMqBusFactoryConfigurator configuration)
    {
        configuration.UseDelayedRedelivery(redeliveryConfiguration =>
        {
            redeliveryConfiguration.Handle<SqlException>();
            redeliveryConfiguration.Handle<HttpRequestException>();
            redeliveryConfiguration.Handle<IOException>();
            redeliveryConfiguration.Handle<TimeoutException>();
            redeliveryConfiguration.Handle<TaskCanceledException>();
            redeliveryConfiguration.Intervals(
                TimeSpan.FromSeconds(15),
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(5));
        });
        configuration.UseMessageRetry(retryConfiguration =>
        {
            retryConfiguration.Handle<SqlException>();
            retryConfiguration.Handle<HttpRequestException>();
            retryConfiguration.Handle<IOException>();
            retryConfiguration.Handle<TimeoutException>();
            retryConfiguration.Handle<TaskCanceledException>();
            retryConfiguration.Interval(3, TimeSpan.FromSeconds(2));
        });
    }
}
