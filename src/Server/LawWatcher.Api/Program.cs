using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using MassTransit;
using LawWatcher.Api.Endpoints;
using LawWatcher.Api.Runtime;
using LawWatcher.AiEnrichment.Application;
using LawWatcher.AiEnrichment.Infrastructure;
using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.BuildingBlocks.Health;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.BuildingBlocks.Ports;
using LawWatcher.IdentityAndAccess.Application;
using LawWatcher.IdentityAndAccess.Infrastructure;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Infrastructure;
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
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.OpenApi;
using System.Security.Claims;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

var dataProtectionKeysDirectory = new DirectoryInfo(Path.Combine(
    builder.Environment.ContentRootPath,
    "artifacts",
    "dataprotection",
    builder.Environment.ApplicationName));
dataProtectionKeysDirectory.Create();

var stateStorageOptions = builder.Configuration.GetSection("LawWatcher:Storage").Get<StateStorageOptions>() ?? new StateStorageOptions();
var objectStorageOptions = builder.Configuration.GetSection("Storage").Get<ObjectStorageOptions>() ?? new ObjectStorageOptions();
var webhookDeliveryOptions = builder.Configuration.GetSection("LawWatcher:Webhooks").Get<WebhookDeliveryOptions>() ?? new WebhookDeliveryOptions();
var persistentStateRoot = StateStoragePathResolver.ResolveRoot(stateStorageOptions, builder.Environment.ContentRootPath);
Directory.CreateDirectory(persistentStateRoot);
var statePaths = LawWatcherStatePaths.ForRoot(persistentStateRoot);
var sqlServerStorageConnectionString = ResolveSqlServerStorageConnectionString(stateStorageOptions, builder.Configuration);
var rabbitMqConnectionString = ResolveRabbitMqConnectionString(builder.Configuration);
var rabbitMqManagementSettings = ResolveRabbitMqManagementConnectionSettings(rabbitMqConnectionString);
var runtimeOptions = builder.Configuration.GetSection("LawWatcher:Runtime").Get<LawWatcherRuntimeOptions>() ?? new LawWatcherRuntimeOptions();
var runtimeProfile = RuntimeProfile.Parse(runtimeOptions.Profile);
var localLlmOptions = builder.Configuration.GetSection("LawWatcher:LocalLlmWorker").Get<LocalLlmWorkerOptions>() ?? new LocalLlmWorkerOptions();
var localEmbeddingOptions = builder.Configuration.GetSection("LawWatcher:LocalEmbedding").Get<LocalEmbeddingOptions>() ?? new LocalEmbeddingOptions();
var ollamaOptions = builder.Configuration.GetSection("AI:Ollama").Get<OllamaOptions>() ?? new OllamaOptions();
var openSearchOptions = builder.Configuration.GetSection("Search:OpenSearch").Get<OpenSearchOptions>() ?? new OpenSearchOptions();
var hostHealthOptions = builder.Configuration.GetSection("LawWatcher:Health").Get<HostHealthOptions>() ?? new HostHealthOptions();
var bootstrapOptions = builder.Configuration.GetSection("LawWatcher:Bootstrap").Get<BootstrapOptions>() ?? new BootstrapOptions();
var enableConfiguredBootstrap = ConfiguredBootstrapHostedServicesPolicy.ShouldRegister(runtimeProfile, bootstrapOptions);
var searchInfrastructureCapabilities = new SearchInfrastructureCapabilities(
    SupportsSqlFullText: DetermineSqlServerFullTextAvailability(stateStorageOptions, sqlServerStorageConnectionString),
    SupportsHybridSearch: DetermineOpenSearchAvailability(runtimeOptions, openSearchOptions, localEmbeddingOptions, ollamaOptions));
var effectiveSearchCapabilities = SearchCapabilitiesRuntimeResolver.Resolve(
    SystemCapabilities.FromOptions(runtimeProfile, runtimeOptions.Capabilities).Search,
    searchInfrastructureCapabilities);
var aiInfrastructureCapabilities = new AiInfrastructureCapabilities(
    SupportsConfiguredLocalLlm: DetermineOllamaModelAvailability(runtimeOptions, localLlmOptions, ollamaOptions));
var ocrInfrastructureCapabilities = new OcrInfrastructureCapabilities(
    SupportsConfiguredDocumentPipeline:
        sqlServerStorageConnectionString is not null
        && rabbitMqConnectionString is not null
        && DocumentStoreRuntimeResolver.Select(objectStorageOptions) == DocumentStoreBackend.S3Compatible);
var readinessState = new HostReadinessState();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddDebug();
}

builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(dataProtectionKeysDirectory);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi("integration-v1", options =>
{
    options.ShouldInclude = description => string.Equals(description.GroupName, "integration", StringComparison.Ordinal);
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "opaque",
            Name = "Authorization",
            In = ParameterLocation.Header,
            Description = "Opaque bearer token issued to a LawWatcher API client."
        };

        document.Security ??= [];
        document.Security.Add(
            new OpenApiSecurityRequirement
            {
                [
                    new OpenApiSecuritySchemeReference("Bearer", document, null)
                ] = []
            });
        return Task.CompletedTask;
    });
});
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "lawwatcher.api.csrf";
    options.HeaderName = "X-LawWatcher-CSRF";
});
builder.Services
    .AddAuthentication(OperatorCookieAuthenticationDefaults.Scheme)
    .AddCookie(OperatorCookieAuthenticationDefaults.Scheme, options =>
    {
        options.Cookie.Name = "lawwatcher.operator.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.Configure<LawWatcherRuntimeOptions>(builder.Configuration.GetSection("LawWatcher:Runtime"));
builder.Services.Configure<StateStorageOptions>(builder.Configuration.GetSection("LawWatcher:Storage"));
builder.Services.Configure<HostHealthOptions>(builder.Configuration.GetSection("LawWatcher:Health"));
builder.Services.Configure<WebhookDeliveryOptions>(builder.Configuration.GetSection("LawWatcher:Webhooks"));
builder.Services.Configure<BootstrapOptions>(builder.Configuration.GetSection("LawWatcher:Bootstrap"));
builder.Services.Configure<ObjectStorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<LocalLlmWorkerOptions>(builder.Configuration.GetSection("LawWatcher:LocalLlmWorker"));
builder.Services.Configure<LocalEmbeddingOptions>(builder.Configuration.GetSection("LawWatcher:LocalEmbedding"));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("AI:Ollama"));
builder.Services.Configure<OpenSearchOptions>(builder.Configuration.GetSection("Search:OpenSearch"));
builder.Services.AddSingleton(readinessState);
builder.Services.AddSingleton<HostReadinessHealthCheck>();
var healthChecks = builder.Services.AddHealthChecks();
healthChecks.AddCheck("self", () => HealthCheckResult.Healthy("API host is running."), tags: [LawWatcherHealthTags.Live]);
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
if (rabbitMqConnectionString is not null)
{
    builder.Services.AddMassTransit(services =>
    {
        services.SetKebabCaseEndpointNameFormatter();
        services.UsingRabbitMq((context, configuration) =>
        {
            ConfigureRabbitMqHost(configuration, rabbitMqConnectionString);
        });
    });
    builder.Services.AddSingleton<IIntegrationEventPublisher, MassTransitIntegrationEventPublisher>();
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<IEventStore>(_ => new SqlServerEventStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IOutboxStore>(_ => new SqlServerOutboxStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IOutboxMessageStore>(_ => new SqlServerOutboxStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IInboxStore>(_ => new SqlServerInboxStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IMessagingDiagnosticsStore>(_ => new SqlServerMessagingDiagnosticsStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IDocumentArtifactCatalog>(_ => new SqlServerDocumentArtifactCatalogStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IRetentionMaintenanceStore>(serviceProvider => new SqlServerRetentionMaintenanceStore(
        sqlServerStorageConnectionString,
        serviceProvider.GetRequiredService<IDocumentStore>(),
        serviceProvider.GetRequiredService<IDocumentArtifactCatalog>()));
}
else
{
    builder.Services.AddSingleton<IDocumentArtifactCatalog>(_ => new FileBackedDocumentArtifactCatalogStore(statePaths.DocumentArtifactsRoot));
    builder.Services.AddSingleton<IMessagingDiagnosticsStore>(_ => new DisabledMessagingDiagnosticsStore());
    builder.Services.AddSingleton<IRetentionMaintenanceStore>(_ => new DisabledRetentionMaintenanceStore());
}
if (rabbitMqManagementSettings is { } brokerDiagnosticsSettings)
{
    builder.Services.AddSingleton<IBrokerDiagnosticsStore>(_ => new RabbitMqBrokerDiagnosticsStore(
        CreateRabbitMqManagementHttpClient(brokerDiagnosticsSettings),
        brokerDiagnosticsSettings.VirtualHost));
}
else
{
    builder.Services.AddSingleton<IBrokerDiagnosticsStore>(_ => new DisabledBrokerDiagnosticsStore());
}
builder.Services.AddSingleton(searchInfrastructureCapabilities);
builder.Services.AddSingleton(aiInfrastructureCapabilities);
builder.Services.AddSingleton(ocrInfrastructureCapabilities);
builder.Services.AddSingleton<ISystemCapabilitiesProvider, ConfigurationSystemCapabilitiesProvider>();
builder.Services.AddSingleton(_ => new MessagingDiagnosticsQueryService(
    _.GetRequiredService<IMessagingDiagnosticsStore>(),
    _.GetRequiredService<IBrokerDiagnosticsStore>(),
    sqlOutboxEnabled: sqlServerStorageConnectionString is not null,
    brokerEnabled: sqlServerStorageConnectionString is not null && rabbitMqConnectionString is not null));
builder.Services.AddSingleton<RetentionMaintenanceCommandService>();
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
if (effectiveSearchCapabilities.UseHybridSearch)
{
    builder.Services.AddSingleton<IEmbeddingService>(serviceProvider =>
    {
        var resolvedOllamaOptions = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OllamaOptions>>().CurrentValue;
        var resolvedEmbeddingOptions = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<LocalEmbeddingOptions>>().CurrentValue;
        return new OllamaEmbeddingService(
            CreateOllamaHttpClient(resolvedOllamaOptions),
            resolvedEmbeddingOptions.DefaultModel);
    });
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<SqlServerApiClientProjectionStore>(_ => new SqlServerApiClientProjectionStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IApiClientProjection>(serviceProvider => serviceProvider.GetRequiredService<SqlServerApiClientProjectionStore>());
    builder.Services.AddSingleton<IApiClientReadRepository>(serviceProvider => serviceProvider.GetRequiredService<SqlServerApiClientProjectionStore>());
    builder.Services.AddSingleton<IApiClientRepository>(serviceProvider => new SqlServerApiClientRepository(
        serviceProvider.GetRequiredService<IEventStore>(),
        sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IApiClientProjection>(_ => new FileBackedApiClientProjectionStore(statePaths.ApiClientsRoot));
    builder.Services.AddSingleton<IApiClientReadRepository>(serviceProvider => serviceProvider.GetRequiredService<IApiClientProjection>() as IApiClientReadRepository
        ?? throw new InvalidOperationException("API client projection store must implement the read repository."));
    builder.Services.AddSingleton<IApiClientRepository>(_ => new FileBackedApiClientRepository(statePaths.ApiClientsRoot));
}
builder.Services.AddSingleton<IApiTokenFingerprintService, Sha256ApiTokenFingerprintService>();
builder.Services.AddSingleton<ApiClientsCommandService>();
builder.Services.AddSingleton<ApiClientsQueryService>();
builder.Services.AddSingleton<ApiClientAccessService>();
builder.Services.AddSingleton<ProductBootstrapService>();
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<SqlServerOperatorAccountProjectionStore>(_ => new SqlServerOperatorAccountProjectionStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IOperatorAccountProjection>(serviceProvider => serviceProvider.GetRequiredService<SqlServerOperatorAccountProjectionStore>());
    builder.Services.AddSingleton<IOperatorAccountReadRepository>(serviceProvider => serviceProvider.GetRequiredService<SqlServerOperatorAccountProjectionStore>());
    builder.Services.AddSingleton<IOperatorAccountRepository>(serviceProvider => new SqlServerOperatorAccountRepository(
        serviceProvider.GetRequiredService<IEventStore>(),
        sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IOperatorAccountProjection>(_ => new FileBackedOperatorAccountProjectionStore(statePaths.OperatorAccountsRoot));
    builder.Services.AddSingleton<IOperatorAccountReadRepository>(serviceProvider => serviceProvider.GetRequiredService<IOperatorAccountProjection>() as IOperatorAccountReadRepository
        ?? throw new InvalidOperationException("Operator account projection store must implement the read repository."));
    builder.Services.AddSingleton<IOperatorAccountRepository>(_ => new FileBackedOperatorAccountRepository(statePaths.OperatorAccountsRoot));
}
builder.Services.AddSingleton<IOperatorPasswordHasher, Pbkdf2OperatorPasswordHasher>();
builder.Services.AddSingleton<OperatorAccountsCommandService>();
builder.Services.AddSingleton<OperatorAccountsQueryService>();
builder.Services.AddSingleton<OperatorAuthenticationService>();
builder.Services.AddSingleton<OperatorAccessService>();
if (sqlServerStorageConnectionString is not null)
{
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
builder.Services.AddSingleton<ILlmService>(serviceProvider =>
{
    var llmWorkerOptions = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<LocalLlmWorkerOptions>>().CurrentValue;
    var resolvedOllamaOptions = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OllamaOptions>>().CurrentValue;
    return new OllamaLlmService(
        CreateOllamaHttpClient(resolvedOllamaOptions),
        llmWorkerOptions.DefaultModel,
        $"{Math.Max(1, llmWorkerOptions.UnloadAfterIdleSeconds)}s");
});
builder.Services.AddSingleton<IAiPromptAugmentor, PassthroughAiPromptAugmentor>();
builder.Services.AddSingleton<AiEnrichmentCommandService>();
builder.Services.AddSingleton<AiEnrichmentExecutionService>();
builder.Services.AddSingleton<AiEnrichmentQueueProcessor>();
builder.Services.AddSingleton<AiEnrichmentTasksQueryService>();
if (sqlServerStorageConnectionString is not null && rabbitMqConnectionString is not null)
{
    builder.Services.AddSingleton<AiEnrichmentRequestedOutboxPublisher>();
    builder.Services.AddHostedService<AiEnrichmentBrokerPublishingHostedService>();
    builder.Services.AddSingleton<ReplayRequestedOutboxPublisher>();
    builder.Services.AddSingleton<BackfillRequestedOutboxPublisher>();
    builder.Services.AddHostedService<ReplayBackfillBrokerPublishingHostedService>();
    builder.Services.AddSingleton<MonitoringProfileProjectionOutboxPublisher>();
    builder.Services.AddHostedService<MonitoringProfileProjectionBrokerPublishingHostedService>();
    builder.Services.AddSingleton<BillProjectionOutboxPublisher>();
    builder.Services.AddHostedService<BillProjectionBrokerPublishingHostedService>();
    builder.Services.AddSingleton<ProcessProjectionOutboxPublisher>();
    builder.Services.AddHostedService<ProcessProjectionBrokerPublishingHostedService>();
    builder.Services.AddSingleton<ActProjectionOutboxPublisher>();
    builder.Services.AddHostedService<ActProjectionBrokerPublishingHostedService>();
    builder.Services.AddSingleton<BillAlertCreatedOutboxPublisher>();
    builder.Services.AddHostedService<BillAlertBrokerPublishingHostedService>();
    builder.Services.AddSingleton<ProfileSubscriptionOutboxPublisher>();
    builder.Services.AddSingleton<WebhookRegistrationOutboxPublisher>();
    builder.Services.AddHostedService<AdminCrudBrokerPublishingHostedService>();
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<SqlServerImportedBillProjectionStore>(_ => new SqlServerImportedBillProjectionStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IImportedBillProjection>(serviceProvider => serviceProvider.GetRequiredService<SqlServerImportedBillProjectionStore>());
    builder.Services.AddSingleton<IImportedBillReadRepository>(serviceProvider => serviceProvider.GetRequiredService<SqlServerImportedBillProjectionStore>());
    builder.Services.AddSingleton<IImportedBillRepository>(serviceProvider => new SqlServerImportedBillRepository(
        serviceProvider.GetRequiredService<IEventStore>(),
        sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IImportedBillProjection>(_ => new FileBackedImportedBillProjectionStore(statePaths.BillsRoot));
    builder.Services.AddSingleton<IImportedBillReadRepository>(serviceProvider => serviceProvider.GetRequiredService<IImportedBillProjection>() as IImportedBillReadRepository
        ?? throw new InvalidOperationException("Imported bill projection store must implement the read repository."));
    builder.Services.AddSingleton<IImportedBillRepository>(_ => new FileBackedImportedBillRepository(statePaths.BillsRoot));
}
builder.Services.AddSingleton<LegislativeIntakeCommandService>();
builder.Services.AddSingleton<BillsQueryService>();
if (enableConfiguredBootstrap)
{
    builder.Services.AddHostedService<LegislativeIntakeBootstrapHostedService>();
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<SqlServerLegislativeProcessProjectionStore>(_ => new SqlServerLegislativeProcessProjectionStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<ILegislativeProcessProjection>(serviceProvider => serviceProvider.GetRequiredService<SqlServerLegislativeProcessProjectionStore>());
    builder.Services.AddSingleton<ILegislativeProcessReadRepository>(serviceProvider => serviceProvider.GetRequiredService<SqlServerLegislativeProcessProjectionStore>());
    builder.Services.AddSingleton<ILegislativeProcessRepository>(serviceProvider => new SqlServerLegislativeProcessRepository(
        serviceProvider.GetRequiredService<IEventStore>(),
        sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<ILegislativeProcessProjection>(_ => new FileBackedLegislativeProcessProjectionStore(statePaths.ProcessesRoot));
    builder.Services.AddSingleton<ILegislativeProcessReadRepository>(serviceProvider => serviceProvider.GetRequiredService<ILegislativeProcessProjection>() as ILegislativeProcessReadRepository
        ?? throw new InvalidOperationException("Legislative process projection store must implement the read repository."));
    builder.Services.AddSingleton<ILegislativeProcessRepository>(_ => new FileBackedLegislativeProcessRepository(statePaths.ProcessesRoot));
}
builder.Services.AddSingleton<LegislativeProcessCommandService>();
builder.Services.AddSingleton<ProcessesQueryService>();
if (enableConfiguredBootstrap)
{
    builder.Services.AddHostedService<LegislativeProcessBootstrapHostedService>();
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<SqlServerPublishedActProjectionStore>(_ => new SqlServerPublishedActProjectionStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IPublishedActProjection>(serviceProvider => serviceProvider.GetRequiredService<SqlServerPublishedActProjectionStore>());
    builder.Services.AddSingleton<IPublishedActReadRepository>(serviceProvider => serviceProvider.GetRequiredService<SqlServerPublishedActProjectionStore>());
    builder.Services.AddSingleton<IPublishedActRepository>(serviceProvider => new SqlServerPublishedActRepository(
        serviceProvider.GetRequiredService<IEventStore>(),
        sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IPublishedActProjection>(_ => new FileBackedPublishedActProjectionStore(statePaths.ActsRoot));
    builder.Services.AddSingleton<IPublishedActReadRepository>(serviceProvider => serviceProvider.GetRequiredService<IPublishedActProjection>() as IPublishedActReadRepository
        ?? throw new InvalidOperationException("Published act projection store must implement the read repository."));
    builder.Services.AddSingleton<IPublishedActRepository>(_ => new FileBackedPublishedActRepository(statePaths.ActsRoot));
}
builder.Services.AddSingleton<LegalCorpusCommandService>();
builder.Services.AddSingleton<ActsQueryService>();
if (enableConfiguredBootstrap)
{
    builder.Services.AddHostedService<LegalCorpusBootstrapHostedService>();
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<SqlServerMonitoringProfileProjectionStore>(_ => new SqlServerMonitoringProfileProjectionStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IMonitoringProfileProjection>(serviceProvider => serviceProvider.GetRequiredService<SqlServerMonitoringProfileProjectionStore>());
    builder.Services.AddSingleton<IMonitoringProfileReadRepository>(serviceProvider => serviceProvider.GetRequiredService<SqlServerMonitoringProfileProjectionStore>());
    builder.Services.AddSingleton<IMonitoringProfileRepository>(serviceProvider => new SqlServerMonitoringProfileRepository(serviceProvider.GetRequiredService<IEventStore>()));
}
else
{
    builder.Services.AddSingleton<IMonitoringProfileProjection>(_ => new FileBackedMonitoringProfileProjectionStore(statePaths.MonitoringProfilesRoot));
    builder.Services.AddSingleton<IMonitoringProfileReadRepository>(serviceProvider => serviceProvider.GetRequiredService<IMonitoringProfileProjection>() as IMonitoringProfileReadRepository
        ?? throw new InvalidOperationException("Monitoring profile projection store must implement the read repository."));
    builder.Services.AddSingleton<IMonitoringProfileRepository>(_ => new FileBackedMonitoringProfileRepository(statePaths.MonitoringProfilesRoot));
}
builder.Services.AddSingleton<MonitoringProfilesCommandService>();
builder.Services.AddSingleton<MonitoringProfilesQueryService>();
if (enableConfiguredBootstrap)
{
    builder.Services.AddHostedService<MonitoringProfilesBootstrapHostedService>();
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<SqlServerProfileSubscriptionProjectionStore>(_ => new SqlServerProfileSubscriptionProjectionStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IProfileSubscriptionProjection>(serviceProvider => serviceProvider.GetRequiredService<SqlServerProfileSubscriptionProjectionStore>());
    builder.Services.AddSingleton<IProfileSubscriptionReadRepository>(serviceProvider => serviceProvider.GetRequiredService<SqlServerProfileSubscriptionProjectionStore>());
    builder.Services.AddSingleton<IProfileSubscriptionRepository>(serviceProvider => new SqlServerProfileSubscriptionRepository(
        serviceProvider.GetRequiredService<IEventStore>(),
        sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IProfileSubscriptionProjection>(_ => new FileBackedProfileSubscriptionProjectionStore(statePaths.ProfileSubscriptionsRoot));
    builder.Services.AddSingleton<IProfileSubscriptionReadRepository>(serviceProvider => serviceProvider.GetRequiredService<IProfileSubscriptionProjection>() as IProfileSubscriptionReadRepository
        ?? throw new InvalidOperationException("Profile subscription projection store must implement the read repository."));
    builder.Services.AddSingleton<IProfileSubscriptionRepository>(_ => new FileBackedProfileSubscriptionRepository(statePaths.ProfileSubscriptionsRoot));
}
builder.Services.AddSingleton<INotificationSubscriptionReadRepository>(serviceProvider =>
    new ProfileSubscriptionNotificationReadRepositoryAdapter(serviceProvider.GetRequiredService<IProfileSubscriptionReadRepository>()));
builder.Services.AddSingleton<ProfileSubscriptionsCommandService>();
builder.Services.AddSingleton<ProfileSubscriptionsQueryService>();
if (enableConfiguredBootstrap)
{
    builder.Services.AddHostedService<ProfileSubscriptionsBootstrapHostedService>();
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<SqlServerWebhookRegistrationProjectionStore>(_ => new SqlServerWebhookRegistrationProjectionStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IWebhookRegistrationProjection>(serviceProvider => serviceProvider.GetRequiredService<SqlServerWebhookRegistrationProjectionStore>());
    builder.Services.AddSingleton<IWebhookRegistrationReadRepository>(serviceProvider => serviceProvider.GetRequiredService<SqlServerWebhookRegistrationProjectionStore>());
    builder.Services.AddSingleton<IWebhookRegistrationRepository>(serviceProvider => new SqlServerWebhookRegistrationRepository(
        serviceProvider.GetRequiredService<IEventStore>(),
        sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IWebhookRegistrationProjection>(_ => new FileBackedWebhookRegistrationProjectionStore(statePaths.WebhookRegistrationsRoot));
    builder.Services.AddSingleton<IWebhookRegistrationReadRepository>(serviceProvider => serviceProvider.GetRequiredService<IWebhookRegistrationProjection>() as IWebhookRegistrationReadRepository
        ?? throw new InvalidOperationException("Webhook registration projection store must implement the read repository."));
    builder.Services.AddSingleton<IWebhookRegistrationRepository>(_ => new FileBackedWebhookRegistrationRepository(statePaths.WebhookRegistrationsRoot));
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<IWebhookEventDispatchStore>(_ => new SqlServerWebhookEventDispatchStore(sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IWebhookEventDispatchStore>(_ => new FileBackedWebhookEventDispatchStore(statePaths.WebhookEventDispatchesRoot));
}
builder.Services.AddSingleton<WebhookRegistrationsCommandService>();
builder.Services.AddSingleton<WebhookRegistrationsQueryService>();
builder.Services.AddSingleton<AlertWebhookDispatchService>();
builder.Services.AddSingleton<WebhookEventDispatchesQueryService>();
if (enableConfiguredBootstrap)
{
    builder.Services.AddHostedService<WebhookRegistrationsBootstrapHostedService>();
}
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
builder.Services.AddSingleton<BackfillRequestsCommandService>();
builder.Services.AddSingleton<BackfillExecutionService>();
builder.Services.AddSingleton<BackfillQueueProcessor>();
builder.Services.AddSingleton<BackfillRequestsQueryService>();
if (enableConfiguredBootstrap)
{
    builder.Services.AddHostedService<BackfillRequestsBootstrapHostedService>();
}
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<SqlServerReplayRequestProjectionStore>(_ => new SqlServerReplayRequestProjectionStore(sqlServerStorageConnectionString));
    builder.Services.AddSingleton<IReplayRequestProjection>(serviceProvider => serviceProvider.GetRequiredService<SqlServerReplayRequestProjectionStore>());
    builder.Services.AddSingleton<IReplayRequestReadRepository>(serviceProvider => serviceProvider.GetRequiredService<SqlServerReplayRequestProjectionStore>());
    builder.Services.AddSingleton<IReplayRequestRepository>(serviceProvider => new SqlServerReplayRequestRepository(
        serviceProvider.GetRequiredService<IEventStore>(),
        sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IReplayRequestProjection>(_ => new FileBackedReplayRequestProjectionStore(statePaths.ReplaysRoot));
    builder.Services.AddSingleton<IReplayRequestReadRepository>(serviceProvider => serviceProvider.GetRequiredService<IReplayRequestProjection>() as IReplayRequestReadRepository
        ?? throw new InvalidOperationException("Replay projection store must implement the read repository."));
    builder.Services.AddSingleton<IReplayRequestRepository>(_ => new FileBackedReplayRequestRepository(statePaths.ReplaysRoot));
}
builder.Services.AddSingleton<ReplayRequestsCommandService>();
builder.Services.AddSingleton<ReplayExecutionService>();
builder.Services.AddSingleton<ReplayQueueProcessor>();
builder.Services.AddSingleton<ReplayRequestsQueryService>();
if (enableConfiguredBootstrap)
{
    builder.Services.AddHostedService<ReplayRequestsBootstrapHostedService>();
}
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
builder.Services.AddSingleton<AlertGenerationService>();
builder.Services.AddSingleton<AlertsQueryService>();
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<IAlertNotificationDispatchStore>(_ => new SqlServerAlertNotificationDispatchStore(sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IAlertNotificationDispatchStore>(_ => new FileBackedAlertNotificationDispatchStore(statePaths.NotificationDispatchesRoot));
}
builder.Services.AddSingleton<InMemoryEmailNotificationChannel>();
builder.Services.AddSingleton<INotificationChannel>(serviceProvider => serviceProvider.GetRequiredService<InMemoryEmailNotificationChannel>());
builder.Services.AddSingleton<INotificationChannel, WebhookNotificationChannel>();
builder.Services.AddSingleton<AlertNotificationDispatchService>();
builder.Services.AddSingleton<AlertNotificationDispatchesQueryService>();
if (sqlServerStorageConnectionString is not null)
{
    builder.Services.AddSingleton<IEventFeedReadRepository>(_ => new SqlServerEventFeedProjectionStore(sqlServerStorageConnectionString));
}
else
{
    builder.Services.AddSingleton<IEventFeedReadRepository>(_ => new FileBackedEventFeedProjectionStore(statePaths.EventFeedRoot));
}
builder.Services.AddSingleton<EventFeedQueryService>();
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
        builder.Services.AddSingleton<SqlServerSearchDocumentIndex>(_ => new SqlServerSearchDocumentIndex(
            sqlServerStorageConnectionString,
            useSqlFullText: effectiveSearchCapabilities.UseSqlFullText));
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
builder.Services.AddSingleton<IWebhookDispatcher>(serviceProvider => WebhookDispatcherRuntimeFactory.Create(
    webhookDeliveryOptions,
    serviceProvider.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<SearchIndexingService>();
builder.Services.AddSingleton<SearchQueryService>();
if (enableConfiguredBootstrap)
{
    builder.Services.AddHostedService<ApiClientsBootstrapHostedService>();
    builder.Services.AddHostedService<OperatorAccountsBootstrapHostedService>();
    builder.Services.AddHostedService<AiEnrichmentBootstrapHostedService>();
}

var app = builder.Build();

if (ShouldUseHttpsRedirection(builder.Configuration))
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapOpenApi("/openapi/{documentName}.json");
app.MapGet(hostHealthOptions.LivePath, async (HttpContext context, HealthCheckService healthCheckService) =>
{
    await LawWatcherHealthResponseWriter.WriteAsync(
        context,
        healthCheckService,
        LawWatcherHealthTags.Live,
        context.RequestAborted);
}).AllowAnonymous();
app.MapGet(hostHealthOptions.ReadyPath, async (HttpContext context, HealthCheckService healthCheckService) =>
{
    await LawWatcherHealthResponseWriter.WriteAsync(
        context,
        healthCheckService,
        LawWatcherHealthTags.Ready,
        context.RequestAborted);
}).AllowAnonymous();
app.MapLawWatcherV1Endpoints();
app.Lifetime.ApplicationStarted.Register(readinessState.MarkReady);

app.Run();

static bool ShouldUseHttpsRedirection(IConfiguration configuration)
{
    var configuredUrls = configuration["ASPNETCORE_URLS"] ?? configuration["urls"] ?? string.Empty;
    return configuredUrls
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
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

static (Uri BaseAddress, string Username, string Password, string VirtualHost)? ResolveRabbitMqManagementConnectionSettings(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return null;
    }

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
    var managementScheme = string.Equals(uri.Scheme, "amqps", StringComparison.OrdinalIgnoreCase)
        ? "https"
        : "http";
    var managementPort = managementScheme == "https" ? 15671 : 15672;

    return (
        new UriBuilder(managementScheme, uri.Host, managementPort).Uri,
        username,
        password,
        virtualHost);
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

static HttpClient CreateRabbitMqManagementHttpClient((Uri BaseAddress, string Username, string Password, string VirtualHost) settings)
{
    var client = new HttpClient
    {
        BaseAddress = settings.BaseAddress,
        Timeout = TimeSpan.FromSeconds(5)
    };
    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}"));
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    return client;
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

static bool DetermineOllamaModelAvailability(
    LawWatcherRuntimeOptions runtimeOptions,
    LocalLlmWorkerOptions localLlmOptions,
    OllamaOptions ollamaOptions)
{
    if (!runtimeOptions.Capabilities.Ai)
    {
        return false;
    }

    try
    {
        using var httpClient = CreateOllamaHttpClient(ollamaOptions);
        var availability = OllamaAvailabilityProbe.CheckAsync(httpClient, localLlmOptions.DefaultModel, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return availability.ServerReachable && availability.ModelAvailable;
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

public partial class Program;
