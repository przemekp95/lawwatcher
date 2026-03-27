using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Infrastructure;
using LawWatcher.AiEnrichment.Infrastructure;
using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegalCorpus.Infrastructure;
using LawWatcher.LegislativeIntake.Application;
using LawWatcher.LegislativeIntake.Infrastructure;
using LawWatcher.LegislativeProcess.Application;
using LawWatcher.LegislativeProcess.Infrastructure;
using LawWatcher.Notifications.Application;
using LawWatcher.Notifications.Infrastructure;
using LawWatcher.SearchAndDiscovery.Application;
using LawWatcher.SearchAndDiscovery.Infrastructure;
using LawWatcher.TaxonomyAndProfiles.Application;
using LawWatcher.TaxonomyAndProfiles.Infrastructure;
using LawWatcher.Worker.Lite;
using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.BuildingBlocks.Health;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.BuildingBlocks.Ports;
using MassTransit;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LawWatcher.Worker.Lite;

public static class WorkerLiteHostApplication
{
    public static IHost Build(string[] args)
    {
var builder = Host.CreateApplicationBuilder(args);

var stateStorageOptions = builder.Configuration.GetSection("LawWatcher:Storage").Get<StateStorageOptions>() ?? new StateStorageOptions();
var webhookDeliveryOptions = builder.Configuration.GetSection("LawWatcher:Webhooks").Get<WebhookDeliveryOptions>() ?? new WebhookDeliveryOptions();
var stateRoot = StateStoragePathResolver.ResolveRoot(stateStorageOptions, builder.Environment.ContentRootPath);
var statePaths = LawWatcherStatePaths.ForRoot(stateRoot);
var sqlServerStorageConnectionString = ResolveSqlServerStorageConnectionString(stateStorageOptions, builder.Configuration);
var rabbitMqConnectionString = ResolveRabbitMqConnectionString(builder.Configuration);
var runtimeOptions = builder.Configuration.GetSection("LawWatcher:Runtime").Get<LawWatcherRuntimeOptions>() ?? new LawWatcherRuntimeOptions();
var localEmbeddingOptions = builder.Configuration.GetSection("LawWatcher:LocalEmbedding").Get<LocalEmbeddingOptions>() ?? new LocalEmbeddingOptions();
var ollamaOptions = builder.Configuration.GetSection("AI:Ollama").Get<OllamaOptions>() ?? new OllamaOptions();
var openSearchOptions = builder.Configuration.GetSection("Search:OpenSearch").Get<OpenSearchOptions>() ?? new OpenSearchOptions();
var effectiveSearchCapabilities = SearchCapabilitiesRuntimeResolver.Resolve(
    SystemCapabilities.FromOptions(RuntimeProfile.Parse(runtimeOptions.Profile), runtimeOptions.Capabilities).Search,
    new SearchInfrastructureCapabilities(
        SupportsSqlFullText: DetermineSqlServerFullTextAvailability(stateStorageOptions, sqlServerStorageConnectionString),
        SupportsHybridSearch: DetermineOpenSearchAvailability(runtimeOptions, openSearchOptions, localEmbeddingOptions, ollamaOptions)));
var enabledPipelines = new HashSet<string>(
    WorkerLitePipelineConfiguration.ResolveEnabledPipelines(builder.Configuration),
    StringComparer.OrdinalIgnoreCase);
var projectionPipelineEnabled = enabledPipelines.Contains("projection");
var notificationsPipelineEnabled = enabledPipelines.Contains("notifications");
var replayPipelineEnabled = enabledPipelines.Contains("replay");
var backfillPipelineEnabled = enabledPipelines.Contains("backfill");
var readinessState = new HostReadinessState();
Directory.CreateDirectory(statePaths.ReplaysRoot);
Directory.CreateDirectory(statePaths.BackfillsRoot);
Directory.CreateDirectory(statePaths.ProfileSubscriptionsRoot);
Directory.CreateDirectory(statePaths.WebhookRegistrationsRoot);
Directory.CreateDirectory(statePaths.BillAlertsRoot);
Directory.CreateDirectory(statePaths.NotificationDispatchesRoot);
Directory.CreateDirectory(statePaths.WebhookEventDispatchesRoot);
Directory.CreateDirectory(statePaths.MonitoringProfilesRoot);
Directory.CreateDirectory(statePaths.BillsRoot);
Directory.CreateDirectory(statePaths.ProcessesRoot);
Directory.CreateDirectory(statePaths.ActsRoot);
Directory.CreateDirectory(statePaths.EventFeedRoot);
Directory.CreateDirectory(statePaths.SearchIndexRoot);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddDebug();
}

builder.Services.Configure<LawWatcherRuntimeOptions>(builder.Configuration.GetSection("LawWatcher:Runtime"));
builder.Services.Configure<StateStorageOptions>(builder.Configuration.GetSection("LawWatcher:Storage"));
builder.Services.Configure<HostHealthOptions>(builder.Configuration.GetSection("LawWatcher:Health"));
builder.Services.Configure<WebhookDeliveryOptions>(builder.Configuration.GetSection("LawWatcher:Webhooks"));
builder.Services.Configure<WorkerLiteOptions>(builder.Configuration.GetSection("LawWatcher:WorkerLite"));
builder.Services.Configure<LocalEmbeddingOptions>(builder.Configuration.GetSection("LawWatcher:LocalEmbedding"));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("AI:Ollama"));
builder.Services.Configure<OpenSearchOptions>(builder.Configuration.GetSection("Search:OpenSearch"));
builder.Services.AddSingleton(readinessState);
builder.Services.AddSingleton<HostReadinessHealthCheck>();
var healthChecks = builder.Services.AddHealthChecks();
healthChecks.AddCheck("self", () => HealthCheckResult.Healthy("Worker.Lite host is running."), tags: [LawWatcherHealthTags.Live]);
healthChecks.AddCheck<HostReadinessHealthCheck>("startup", tags: [LawWatcherHealthTags.Ready]);
if (sqlServerStorageConnectionString is not null)
{
    healthChecks.AddCheck("sqlserver", new SqlServerConnectionHealthCheck(sqlServerStorageConnectionString), tags: [LawWatcherHealthTags.Ready]);
}
if (rabbitMqConnectionString is not null)
{
    healthChecks.AddCheck("rabbitmq", new RabbitMqConnectionHealthCheck(rabbitMqConnectionString), tags: [LawWatcherHealthTags.Ready]);
}
if (effectiveSearchCapabilities.UseHybridSearch)
{
    healthChecks.AddCheck("opensearch", new OpenSearchConnectionHealthCheck(openSearchOptions.BaseUrl), tags: [LawWatcherHealthTags.Ready]);
}
if (sqlServerStorageConnectionString is not null &&
    rabbitMqConnectionString is not null &&
    (projectionPipelineEnabled || notificationsPipelineEnabled || replayPipelineEnabled || backfillPipelineEnabled))
{
    builder.Services.AddMassTransit(services =>
    {
        services.SetKebabCaseEndpointNameFormatter();
        if (replayPipelineEnabled)
        {
            services.AddConsumer<ReplayRequestedConsumer>();
        }

        if (backfillPipelineEnabled)
        {
            services.AddConsumer<BackfillRequestedConsumer>();
        }

        if (projectionPipelineEnabled)
        {
            services.AddConsumer<MonitoringProfileCreatedConsumer>();
            services.AddConsumer<MonitoringProfileRuleAddedConsumer>();
            services.AddConsumer<MonitoringProfileAlertPolicyChangedConsumer>();
            services.AddConsumer<MonitoringProfileDeactivatedConsumer>();
            services.AddConsumer<BillImportedConsumer>();
            services.AddConsumer<BillDocumentAttachedConsumer>();
            services.AddConsumer<LegislativeProcessStartedConsumer>();
            services.AddConsumer<LegislativeStageRecordedConsumer>();
            services.AddConsumer<PublishedActRegisteredConsumer>();
            services.AddConsumer<ActArtifactAttachedConsumer>();
        }

        if (notificationsPipelineEnabled)
        {
            services.AddConsumer<BillAlertNotificationDispatchConsumer>();
            services.AddConsumer<BillAlertWebhookDispatchConsumer>();
            services.AddConsumer<ProfileSubscriptionNotificationConsumer>();
            services.AddConsumer<WebhookRegistrationDispatchConsumer>();
        }

        services.UsingRabbitMq((context, configuration) =>
        {
            ConfigureRabbitMqHost(configuration, rabbitMqConnectionString);
            configuration.UseMessageRetry(retryConfiguration =>
            {
                retryConfiguration.Handle<SqlException>(exception => exception.Number == 1205);
                retryConfiguration.Interval(5, TimeSpan.FromSeconds(2));
            });
            configuration.ConfigureEndpoints(context);
        });
    });

    if (replayPipelineEnabled)
    {
        builder.Services.AddSingleton<ReplayRequestedMessageHandler>();
    }

    if (backfillPipelineEnabled)
    {
        builder.Services.AddSingleton<BackfillRequestedMessageHandler>();
    }

    if (projectionPipelineEnabled)
    {
        builder.Services.AddSingleton<MonitoringProfileProjectionRefreshOrchestrator>();
        builder.Services.AddSingleton<IMonitoringProfileProjectionRefreshOrchestrator>(serviceProvider => serviceProvider.GetRequiredService<MonitoringProfileProjectionRefreshOrchestrator>());
        builder.Services.AddSingleton<MonitoringProfileProjectionMessageHandler>();
        builder.Services.AddSingleton<BillProjectionRefreshOrchestrator>();
        builder.Services.AddSingleton<IBillProjectionRefreshOrchestrator>(serviceProvider => serviceProvider.GetRequiredService<BillProjectionRefreshOrchestrator>());
        builder.Services.AddSingleton<BillProjectionMessageHandler>();
        builder.Services.AddSingleton<ProcessProjectionRefreshOrchestrator>();
        builder.Services.AddSingleton<IProcessProjectionRefreshOrchestrator>(serviceProvider => serviceProvider.GetRequiredService<ProcessProjectionRefreshOrchestrator>());
        builder.Services.AddSingleton<ProcessProjectionMessageHandler>();
        builder.Services.AddSingleton<ActProjectionRefreshOrchestrator>();
        builder.Services.AddSingleton<IActProjectionRefreshOrchestrator>(serviceProvider => serviceProvider.GetRequiredService<ActProjectionRefreshOrchestrator>());
        builder.Services.AddSingleton<ActProjectionMessageHandler>();
    }

    if (notificationsPipelineEnabled)
    {
        builder.Services.AddSingleton<BillAlertNotificationMessageHandler>();
        builder.Services.AddSingleton<BillAlertWebhookMessageHandler>();
        builder.Services.AddSingleton<ProfileSubscriptionNotificationMessageHandler>();
        builder.Services.AddSingleton<WebhookRegistrationDispatchMessageHandler>();
    }
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<IMonitoringProfileReadRepository>(_ => new SqlServerMonitoringProfileProjectionStore(sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IMonitoringProfileReadRepository>(_ => new FileBackedMonitoringProfileProjectionStore(statePaths.MonitoringProfilesRoot));
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<IImportedBillReadRepository>(_ => new SqlServerImportedBillProjectionStore(sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IImportedBillReadRepository>(_ => new FileBackedImportedBillProjectionStore(statePaths.BillsRoot));
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<ILegislativeProcessReadRepository>(_ => new SqlServerLegislativeProcessProjectionStore(sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<ILegislativeProcessReadRepository>(_ => new FileBackedLegislativeProcessProjectionStore(statePaths.ProcessesRoot));
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<IPublishedActReadRepository>(_ => new SqlServerPublishedActProjectionStore(sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IPublishedActReadRepository>(_ => new FileBackedPublishedActProjectionStore(statePaths.ActsRoot));
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<IProfileSubscriptionReadRepository>(_ => new SqlServerProfileSubscriptionProjectionStore(sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IProfileSubscriptionProjection>(_ => new FileBackedProfileSubscriptionProjectionStore(statePaths.ProfileSubscriptionsRoot));
    builder.Services.AddSingleton<IProfileSubscriptionReadRepository>(serviceProvider => serviceProvider.GetRequiredService<IProfileSubscriptionProjection>() as IProfileSubscriptionReadRepository
        ?? throw new InvalidOperationException("Profile subscription projection store must implement the read repository."));
}
builder.Services.AddSingleton<INotificationSubscriptionReadRepository>(serviceProvider =>
    new ProfileSubscriptionNotificationReadRepositoryAdapter(serviceProvider.GetRequiredService<IProfileSubscriptionReadRepository>()));
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<IEventStore>(_ => new SqlServerEventStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IOutboxMessageStore>(_ => new SqlServerOutboxStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IInboxStore>(_ => new SqlServerInboxStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<SqlServerWebhookRegistrationProjectionStore>(_ => new SqlServerWebhookRegistrationProjectionStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IWebhookRegistrationProjection>(serviceProvider => serviceProvider.GetRequiredService<SqlServerWebhookRegistrationProjectionStore>());
    builder.Services.AddSingleton<IWebhookRegistrationReadRepository>(serviceProvider => serviceProvider.GetRequiredService<SqlServerWebhookRegistrationProjectionStore>());
    builder.Services.AddSingleton<SqlServerReplayRequestProjectionStore>(_ => new SqlServerReplayRequestProjectionStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IReplayRequestProjection>(serviceProvider => serviceProvider.GetRequiredService<SqlServerReplayRequestProjectionStore>());
    builder.Services.AddSingleton<IReplayRequestReadRepository>(serviceProvider => serviceProvider.GetRequiredService<SqlServerReplayRequestProjectionStore>());
    builder.Services.AddSingleton<IReplayRequestRepository>(serviceProvider => new SqlServerReplayRequestRepository(
        serviceProvider.GetRequiredService<IEventStore>(),
        sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IWebhookRegistrationProjection>(_ => new FileBackedWebhookRegistrationProjectionStore(statePaths.WebhookRegistrationsRoot));
    builder.Services.AddSingleton<IWebhookRegistrationReadRepository>(serviceProvider => serviceProvider.GetRequiredService<IWebhookRegistrationProjection>() as IWebhookRegistrationReadRepository
        ?? throw new InvalidOperationException("Webhook registration projection store must implement the read repository."));
    builder.Services.AddSingleton<IReplayRequestProjection>(_ => new FileBackedReplayRequestProjectionStore(statePaths.ReplaysRoot));
    builder.Services.AddSingleton<IReplayRequestReadRepository>(serviceProvider => serviceProvider.GetRequiredService<IReplayRequestProjection>() as IReplayRequestReadRepository
        ?? throw new InvalidOperationException("Replay projection store must implement the read repository."));
    builder.Services.AddSingleton<IReplayRequestRepository>(_ => new FileBackedReplayRequestRepository(statePaths.ReplaysRoot));
}
builder.Services.AddSingleton<ReplayRequestsQueryService>();
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<SqlServerBackfillRequestProjectionStore>(_ => new SqlServerBackfillRequestProjectionStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IBackfillRequestProjection>(serviceProvider => serviceProvider.GetRequiredService<SqlServerBackfillRequestProjectionStore>());
    builder.Services.AddSingleton<IBackfillRequestReadRepository>(serviceProvider => serviceProvider.GetRequiredService<SqlServerBackfillRequestProjectionStore>());
    builder.Services.AddSingleton<IBackfillRequestRepository>(serviceProvider => new SqlServerBackfillRequestRepository(
        serviceProvider.GetRequiredService<IEventStore>(),
        sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IBackfillRequestProjection>(_ => new FileBackedBackfillRequestProjectionStore(statePaths.BackfillsRoot));
    builder.Services.AddSingleton<IBackfillRequestReadRepository>(serviceProvider => serviceProvider.GetRequiredService<IBackfillRequestProjection>() as IBackfillRequestReadRepository
        ?? throw new InvalidOperationException("Backfill projection store must implement the read repository."));
    builder.Services.AddSingleton<IBackfillRequestRepository>(_ => new FileBackedBackfillRequestRepository(statePaths.BackfillsRoot));
}
builder.Services.AddSingleton<BackfillRequestsQueryService>();
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<SqlServerBillAlertProjectionStore>(_ => new SqlServerBillAlertProjectionStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IBillAlertProjection>(serviceProvider => serviceProvider.GetRequiredService<SqlServerBillAlertProjectionStore>());
    builder.Services.AddSingleton<IBillAlertReadRepository>(serviceProvider => serviceProvider.GetRequiredService<SqlServerBillAlertProjectionStore>());
    builder.Services.AddSingleton<IBillAlertRepository>(_ => new SqlServerBillAlertRepository(sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IBillAlertProjection>(_ => new FileBackedBillAlertProjectionStore(statePaths.BillAlertsRoot));
    builder.Services.AddSingleton<IBillAlertReadRepository>(serviceProvider => serviceProvider.GetRequiredService<IBillAlertProjection>() as IBillAlertReadRepository
        ?? throw new InvalidOperationException("Bill alert projection store must implement the read repository."));
    builder.Services.AddSingleton<IBillAlertRepository>(_ => new FileBackedBillAlertRepository(statePaths.BillAlertsRoot));
}
builder.Services.AddSingleton<IWebhookAlertReadRepository>(serviceProvider =>
    new BillAlertWebhookReadRepositoryAdapter(serviceProvider.GetRequiredService<IBillAlertReadRepository>()));
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<IAlertNotificationDispatchStore>(_ => new SqlServerAlertNotificationDispatchStore(sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IAlertNotificationDispatchStore>(_ => new FileBackedAlertNotificationDispatchStore(statePaths.NotificationDispatchesRoot));
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<IWebhookEventDispatchStore>(_ => new SqlServerWebhookEventDispatchStore(sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IWebhookEventDispatchStore>(_ => new FileBackedWebhookEventDispatchStore(statePaths.WebhookEventDispatchesRoot));
}
builder.Services.AddSingleton<IEmbeddingService>(serviceProvider =>
{
    if (!effectiveSearchCapabilities.UseHybridSearch)
    {
        return new DeterministicEmbeddingService();
    }

    var resolvedOllamaOptions = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OllamaOptions>>().CurrentValue;
    var resolvedEmbeddingOptions = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<LocalEmbeddingOptions>>().CurrentValue;
    return new OllamaEmbeddingService(
        CreateOllamaHttpClient(resolvedOllamaOptions),
        resolvedEmbeddingOptions.DefaultModel);
});
if (sqlServerStorageConnectionString is not null)
{
    if (effectiveSearchCapabilities.UseHybridSearch)
    {
        builder.Services.AddSingleton<OpenSearchSearchDocumentIndex>(serviceProvider =>
        {
            var resolvedOpenSearchOptions = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OpenSearchOptions>>().CurrentValue;
            return new OpenSearchSearchDocumentIndex(
                CreateOpenSearchHttpClient(resolvedOpenSearchOptions),
                serviceProvider.GetRequiredService<IEmbeddingService>(),
                resolvedOpenSearchOptions);
        });
        builder.Services.AddSingleton<ISearchDocumentIndex>(serviceProvider => serviceProvider.GetRequiredService<OpenSearchSearchDocumentIndex>());
        builder.Services.AddSingleton<ISearchIndexer>(serviceProvider => serviceProvider.GetRequiredService<OpenSearchSearchDocumentIndex>());
    }
    else
    {
        builder.Services.AddSingleton<SqlServerSearchDocumentIndex>(_ => new SqlServerSearchDocumentIndex(sqlServerStorageConnectionString));
        builder.Services.AddSingleton<ISearchDocumentIndex>(serviceProvider => serviceProvider.GetRequiredService<SqlServerSearchDocumentIndex>());
        builder.Services.AddSingleton<ISearchIndexer>(serviceProvider => serviceProvider.GetRequiredService<SqlServerSearchDocumentIndex>());
    }
}
else
{
    builder.Services.AddSingleton<ISearchDocumentIndex>(_ => new FileBackedSearchDocumentIndex(statePaths.SearchIndexRoot));
    builder.Services.AddSingleton<ISearchIndexer>(serviceProvider => serviceProvider.GetRequiredService<ISearchDocumentIndex>() as ISearchIndexer
        ?? throw new InvalidOperationException("Search document index must implement the search indexer port."));
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<IEventFeedProjection>(_ => new SqlServerEventFeedProjectionStore(sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IEventFeedProjection>(_ => new FileBackedEventFeedProjectionStore(statePaths.EventFeedRoot));
}
builder.Services.AddSingleton<IEventFeedSource, BillsEventFeedSource>();
builder.Services.AddSingleton<IEventFeedSource, ProcessesEventFeedSource>();
builder.Services.AddSingleton<IEventFeedSource, ActsEventFeedSource>();
builder.Services.AddSingleton<IEventFeedSource, AlertsEventFeedSource>();
builder.Services.AddSingleton<IEventFeedSource, ReplaysEventFeedSource>();
builder.Services.AddSingleton<IEventFeedSource, BackfillsEventFeedSource>();
builder.Services.AddSingleton<InMemoryEmailNotificationChannel>();
builder.Services.AddSingleton<IWebhookDispatcher>(_ => WebhookDispatcherRuntimeFactory.Create(webhookDeliveryOptions));
builder.Services.AddSingleton<INotificationChannel>(serviceProvider => serviceProvider.GetRequiredService<InMemoryEmailNotificationChannel>());
builder.Services.AddSingleton<INotificationChannel, WebhookNotificationChannel>();
builder.Services.AddSingleton<BillsQueryService>();
builder.Services.AddSingleton<ProcessesQueryService>();
builder.Services.AddSingleton<ActsQueryService>();
builder.Services.AddSingleton<MonitoringProfilesQueryService>();
builder.Services.AddSingleton<AlertsQueryService>();
builder.Services.AddSingleton<AlertGenerationService>();
builder.Services.AddSingleton<AlertProjectionRefreshService>();
builder.Services.AddSingleton<EventFeedProjectionRefreshService>();
builder.Services.AddSingleton<SearchIndexingService>();
builder.Services.AddSingleton<SearchProjectionRefreshService>();
builder.Services.AddSingleton<MonitoringProfileProjectionOutboxProcessor>();
builder.Services.AddSingleton<BillProjectionOutboxProcessor>();
builder.Services.AddSingleton<ProcessProjectionOutboxProcessor>();
builder.Services.AddSingleton<ActProjectionOutboxProcessor>();
builder.Services.AddSingleton<ProfileSubscriptionNotificationOutboxProcessor>();
builder.Services.AddSingleton<WebhookRegistrationDispatchOutboxProcessor>();
builder.Services.AddSingleton<ReplayExecutionService>();
builder.Services.AddSingleton<ReplayQueueProcessor>();
builder.Services.AddSingleton<BackfillExecutionService>();
builder.Services.AddSingleton<BackfillQueueProcessor>();
builder.Services.AddSingleton<AlertCreatedOutboxProcessor>();
builder.Services.AddSingleton<AlertNotificationDispatchService>();
builder.Services.AddSingleton<AlertWebhookDispatchService>();
builder.Services.AddHostedService<WorkerHealthServerHostedService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(readinessState.MarkReady);
return host;
    }

static string? ResolveSqlServerStorageConnectionString(StateStorageOptions options, IConfiguration configuration)
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

static string? ResolveRabbitMqConnectionString(IConfiguration configuration)
{
    var connectionString = configuration.GetConnectionString("RabbitMq");
    return string.IsNullOrWhiteSpace(connectionString)
        ? null
        : connectionString;
}

static bool DetermineSqlServerFullTextAvailability(StateStorageOptions options, string? connectionString)
{
    if (!string.Equals(options.Provider, "sqlserver", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(connectionString))
    {
        return false;
    }

    try
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(int, ISNULL(SERVERPROPERTY('IsFullTextInstalled'), 0));";
        var scalar = command.ExecuteScalar();
        return Convert.ToInt32(scalar) == 1;
    }
    catch
    {
        return false;
    }
}

static bool DetermineOpenSearchAvailability(
    LawWatcherRuntimeOptions runtimeOptions,
    OpenSearchOptions openSearchOptions,
    LocalEmbeddingOptions localEmbeddingOptions,
    OllamaOptions ollamaOptions)
{
    if (!runtimeOptions.Capabilities.HybridSearch || string.IsNullOrWhiteSpace(openSearchOptions.BaseUrl))
    {
        return false;
    }

    try
    {
        using var openSearchHttpClient = CreateOpenSearchHttpClient(openSearchOptions);
        var openSearchAvailable = OpenSearchAvailabilityProbe.CheckAsync(openSearchHttpClient, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (!openSearchAvailable)
        {
            return false;
        }

        using var ollamaHttpClient = CreateOllamaHttpClient(ollamaOptions);
        var embeddingAvailability = OllamaAvailabilityProbe.CheckAsync(
                ollamaHttpClient,
                localEmbeddingOptions.DefaultModel,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        return embeddingAvailability.ServerReachable && embeddingAvailability.ModelAvailable;
    }
    catch
    {
        return false;
    }
}

static HttpClient CreateOllamaHttpClient(OllamaOptions options)
{
    return new HttpClient
    {
        BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute),
        Timeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds))
    };
}

static HttpClient CreateOpenSearchHttpClient(OpenSearchOptions options)
{
    return new HttpClient
    {
        BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute),
        Timeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds))
    };
}

static void ConfigureRabbitMqHost(IRabbitMqBusFactoryConfigurator configuration, string connectionString)
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
}
