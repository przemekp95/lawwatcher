using LawWatcher.AiEnrichment.Application;
using LawWatcher.AiEnrichment.Infrastructure;
using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.BuildingBlocks.Health;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.BuildingBlocks.Ports;
using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegalCorpus.Infrastructure;
using MassTransit;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.IO;

namespace LawWatcher.Worker.Documents;

public static class WorkerDocumentsHostApplication
{
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        var stateStorageOptions = builder.Configuration.GetSection("LawWatcher:Storage").Get<StateStorageOptions>() ?? new StateStorageOptions();
        var objectStorageOptions = builder.Configuration.GetSection("Storage").Get<ObjectStorageOptions>() ?? new ObjectStorageOptions();
        var stateRoot = StateStoragePathResolver.ResolveRoot(stateStorageOptions, builder.Environment.ContentRootPath);
        var statePaths = LawWatcherStatePaths.ForRoot(stateRoot);
        var sqlServerStorageConnectionString = ResolveSqlServerStorageConnectionString(stateStorageOptions, builder.Configuration);
        var rabbitMqConnectionString = ResolveRabbitMqConnectionString(builder.Configuration);
        var readinessState = new HostReadinessState();
        Directory.CreateDirectory(statePaths.DocumentArtifactsRoot);

        if (sqlServerStorageConnectionString is null)
        {
            throw new InvalidOperationException("Worker.Documents requires SQL-backed storage for inbox idempotency and document artifact metadata.");
        }
        var requiredSqlServerConnectionString = sqlServerStorageConnectionString;

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
        builder.Services.AddSingleton(readinessState);
        builder.Services.AddSingleton<HostReadinessHealthCheck>();
        var healthChecks = builder.Services.AddHealthChecks();
        healthChecks.AddCheck("self", () => HealthCheckResult.Healthy("Worker.Documents host is running."), tags: [LawWatcherHealthTags.Live]);
        healthChecks.AddCheck<HostReadinessHealthCheck>("startup", tags: [LawWatcherHealthTags.Ready]);
        if (sqlServerStorageConnectionString is not null)
        {
            healthChecks.AddCheck("sqlserver", new SqlServerConnectionHealthCheck(sqlServerStorageConnectionString), tags: [LawWatcherHealthTags.Ready]);
        }

        if (rabbitMqConnectionString is not null)
        {
            healthChecks.AddCheck("rabbitmq", new RabbitMqConnectionHealthCheck(rabbitMqConnectionString), tags: [LawWatcherHealthTags.Ready]);
        }

        if (rabbitMqConnectionString is null)
        {
            throw new InvalidOperationException("Worker.Documents requires RabbitMQ broker transport.");
        }

        builder.Services.AddMassTransit(services =>
        {
            services.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter("worker-documents", false));
            services.AddConsumer<ActArtifactAttachedConsumer>();
            services.AddConsumer<BillDocumentAttachedConsumer>();
            services.UsingRabbitMq((context, configuration) =>
            {
                ConfigureRabbitMqHost(configuration, rabbitMqConnectionString);
                ConfigureBrokerConsumerResiliency(configuration);
                configuration.ConfigureEndpoints(context);
            });
        });

        builder.Services.AddSingleton<IEventStore>(_ => new SqlServerEventStore(requiredSqlServerConnectionString));
        builder.Services.AddSingleton<IInboxStore>(_ => new SqlServerInboxStore(requiredSqlServerConnectionString));
        builder.Services.AddSingleton<IDocumentArtifactCatalog>(_ => new SqlServerDocumentArtifactCatalogStore(requiredSqlServerConnectionString));
        builder.Services.AddSingleton<IPublishedActRepository>(serviceProvider => new SqlServerPublishedActRepository(
            serviceProvider.GetRequiredService<IEventStore>(),
            requiredSqlServerConnectionString));
        builder.Services.AddSingleton<IPublishedActReadRepository>(_ => new SqlServerPublishedActProjectionStore(requiredSqlServerConnectionString));

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
        builder.Services.AddSingleton<IOcrService, ContainerizedDocumentOcrService>();
        builder.Services.AddSingleton<IIntegrationEventPublisher, MassTransitIntegrationEventPublisher>();
        builder.Services.AddSingleton<ActsQueryService>();
        builder.Services.AddSingleton<DocumentProcessingService>();
        builder.Services.AddSingleton<DocumentProcessingMessageHandler>();
        builder.Services.AddSingleton<DocumentArtifactCatchUpService>();
        builder.Services.AddHostedService<WorkerHealthServerHostedService>();
        builder.Services.AddHostedService<DocumentArtifactCatchUpHostedService>();

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
            redeliveryConfiguration.Handle<DerivedDocumentTextNotReadyException>();
            redeliveryConfiguration.Handle<SqlException>();
            redeliveryConfiguration.Handle<HttpRequestException>();
            redeliveryConfiguration.Handle<IOException>();
            redeliveryConfiguration.Handle<TimeoutException>();
            redeliveryConfiguration.Handle<TaskCanceledException>();
            redeliveryConfiguration.Intervals(BrokerConsumerResiliency.DelayedRedeliveryIntervals);
        });
        configuration.UseMessageRetry(retryConfiguration =>
        {
            retryConfiguration.Handle<DerivedDocumentTextNotReadyException>();
            retryConfiguration.Handle<SqlException>();
            retryConfiguration.Handle<HttpRequestException>();
            retryConfiguration.Handle<IOException>();
            retryConfiguration.Handle<TimeoutException>();
            retryConfiguration.Handle<TaskCanceledException>();
            retryConfiguration.Interval(
                BrokerConsumerResiliency.ImmediateRetryCount,
                BrokerConsumerResiliency.ImmediateRetryInterval);
        });
    }

    private sealed class MassTransitIntegrationEventPublisher(IBus bus) : IIntegrationEventPublisher
    {
        public Task PublishAsync<TIntegrationEvent>(TIntegrationEvent integrationEvent, CancellationToken cancellationToken)
            where TIntegrationEvent : class, IIntegrationEvent
        {
            ArgumentNullException.ThrowIfNull(integrationEvent);
            return bus.Publish(integrationEvent, cancellationToken);
        }
    }
}
