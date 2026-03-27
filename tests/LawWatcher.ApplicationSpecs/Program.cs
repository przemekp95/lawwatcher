using LawWatcher.AiEnrichment.Application;
using LawWatcher.AiEnrichment.Contracts;
using LawWatcher.AiEnrichment.Domain.Tasks;
using LawWatcher.AiEnrichment.Infrastructure;
using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.BuildingBlocks.Ports;
using LawWatcher.IdentityAndAccess.Application;
using LawWatcher.IdentityAndAccess.Domain.ApiClients;
using LawWatcher.IdentityAndAccess.Domain.OperatorAccounts;
using LawWatcher.IdentityAndAccess.Infrastructure;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Contracts;
using LawWatcher.IntegrationApi.Domain.Backfills;
using LawWatcher.IntegrationApi.Domain.Replays;
using LawWatcher.IntegrationApi.Domain.Webhooks;
using LawWatcher.IntegrationApi.Infrastructure;
using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegalCorpus.Contracts;
using LawWatcher.LegalCorpus.Domain.Acts;
using LawWatcher.LegalCorpus.Infrastructure;
using LawWatcher.LegislativeIntake.Application;
using LawWatcher.LegislativeIntake.Contracts;
using LawWatcher.LegislativeIntake.Domain.Bills;
using LawWatcher.LegislativeIntake.Infrastructure;
using LawWatcher.LegislativeProcess.Application;
using LawWatcher.LegislativeProcess.Contracts;
using LawWatcher.LegislativeProcess.Domain.Processes;
using LawWatcher.LegislativeProcess.Infrastructure;
using LawWatcher.Notifications.Application;
using LawWatcher.Notifications.Contracts;
using LawWatcher.Notifications.Infrastructure;
using LawWatcher.SearchAndDiscovery.Application;
using LawWatcher.SearchAndDiscovery.Contracts;
using LawWatcher.SearchAndDiscovery.Domain;
using LawWatcher.SearchAndDiscovery.Infrastructure;
using LawWatcher.TaxonomyAndProfiles.Application;
using LawWatcher.TaxonomyAndProfiles.Contracts;
using LawWatcher.TaxonomyAndProfiles.Domain.MonitoringProfiles;
using LawWatcher.TaxonomyAndProfiles.Domain.Subscriptions;
using LawWatcher.TaxonomyAndProfiles.Infrastructure;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

var failures = new List<string>();

var devLaptop = RuntimeProfile.Parse("dev-laptop");
Expect.Equal(RuntimeProfile.DevLaptop, devLaptop, "Runtime profile parser should recognize dev-laptop.", failures);

var capabilities = SystemCapabilities.FromOptions(
    devLaptop,
    new CapabilityOptions
    {
        Ai = true,
        Ocr = true,
        Replay = true,
        SemanticSearch = false,
        HybridSearch = false
    });

Expect.True(capabilities.Ai.Enabled, "AI should remain enabled on laptop-first profile.", failures);
Expect.Equal(AiActivationMode.OnDemand, capabilities.Ai.ActivationMode, "Laptop-first profile should use on-demand local LLM activation.", failures);
Expect.Equal(TimeSpan.FromMinutes(2), capabilities.Ai.UnloadAfterIdle, "Laptop-first profile should unload the local model after a short idle period.", failures);
Expect.True(capabilities.Search.UseSqlFullText, "SQL Full-Text should be the baseline search backend on laptop-first profile.", failures);
Expect.False(capabilities.Search.UseHybridSearch, "Hybrid search should stay disabled on laptop-first profile by default.", failures);

var selector = new SearchBackendSelector();
Expect.Equal(SearchBackend.SqlFullText, selector.Select(capabilities.Search), "Search backend selector should prefer SQL Full-Text when hybrid search is unavailable.", failures);

var degradedSearchCapabilities = SearchCapabilitiesRuntimeResolver.Resolve(
    capabilities.Search,
    new SearchInfrastructureCapabilities(
        SupportsSqlFullText: false,
        SupportsHybridSearch: false));
Expect.False(degradedSearchCapabilities.UseSqlFullText, "Effective search capabilities should disable SQL Full-Text when the runtime cannot provide it.", failures);
Expect.Equal(SearchBackend.ProjectionIndex, selector.Select(degradedSearchCapabilities), "Search backend selector should fall back to the projection index when hybrid search is disabled and SQL Full-Text is unavailable.", failures);
Expect.Equal(
    "\"VAT*\" OR \"JPK_V7*\"",
    SqlServerFullTextSearchConditionBuilder.Build(" VAT  JPK_V7 ") ?? string.Empty,
    "SQL full-text search condition builder should normalize and OR distinct search terms.",
    failures);
Expect.Equal(
    "\"2026*\" OR \"502*\"",
    SqlServerFullTextSearchConditionBuilder.Build("2026/502") ?? string.Empty,
    "SQL full-text search condition builder should split punctuation-delimited terms for native SQL Full-Text queries.",
    failures);

var fullHostCapabilities = SystemCapabilities.FromOptions(
    RuntimeProfile.FullHost,
    new CapabilityOptions
    {
        Ai = true,
        Ocr = true,
        Replay = true,
        SemanticSearch = true,
        HybridSearch = true
    });

Expect.Equal(SearchBackend.HybridVector, selector.Select(fullHostCapabilities.Search), "Search backend selector should use hybrid/vector mode when full-host capabilities enable it.", failures);

var degradedFullHostCapabilities = SearchCapabilitiesRuntimeResolver.Resolve(
    fullHostCapabilities.Search,
    new SearchInfrastructureCapabilities(
        SupportsSqlFullText: true,
        SupportsHybridSearch: false));
Expect.False(degradedFullHostCapabilities.UseHybridSearch, "Effective search capabilities should disable hybrid search when OpenSearch runtime support is unavailable.", failures);
Expect.False(degradedFullHostCapabilities.UseSemanticSearch, "Effective search capabilities should disable semantic search when OpenSearch runtime support is unavailable.", failures);
Expect.Equal(SearchBackend.SqlFullText, selector.Select(degradedFullHostCapabilities), "Search backend selector should fall back to SQL Full-Text when hybrid search is configured but OpenSearch support is unavailable.", failures);

var llmPolicy = LocalLlmExecutionPolicy.For(RuntimeProfile.DevLaptop);
Expect.Equal(AiActivationMode.OnDemand, llmPolicy.ActivationMode, "Local LLM execution policy should stay on-demand for the laptop profile.", failures);
Expect.Equal(1, llmPolicy.MaxConcurrency, "Laptop-first AI worker must stay single-flight.", failures);

var ollamaProbeHandler = new StubSequenceHttpMessageHandler(_ =>
    new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {
              "models": [
                { "name": "llama3.2:1b" },
                { "name": "nomic-embed-text:latest" }
              ]
            }
            """, Encoding.UTF8, "application/json")
    });
var ollamaProbeAvailability = await OllamaAvailabilityProbe.CheckAsync(
    new HttpClient(ollamaProbeHandler)
    {
        BaseAddress = new Uri("http://127.0.0.1:11434")
    },
    "llama3.2:1b",
    CancellationToken.None);

Expect.True(ollamaProbeAvailability.ServerReachable, "Ollama availability probe should mark the local runtime reachable when /api/tags returns successfully.", failures);
Expect.True(ollamaProbeAvailability.ModelAvailable, "Ollama availability probe should detect the configured model in the returned model list.", failures);

var ollamaEmbeddingProbeAvailability = await OllamaAvailabilityProbe.CheckAsync(
    new HttpClient(new StubSequenceHttpMessageHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "models": [
                    { "name": "nomic-embed-text:latest" }
                  ]
                }
                """, Encoding.UTF8, "application/json")
        }))
    {
        BaseAddress = new Uri("http://127.0.0.1:11434")
    },
    "nomic-embed-text",
    CancellationToken.None);

Expect.True(ollamaEmbeddingProbeAvailability.ModelAvailable, "Ollama availability probe should treat an untagged configured model as matching the :latest tag exposed by Ollama.", failures);

var messagingDiagnosticsQueryService = new MessagingDiagnosticsQueryService(
    new StubMessagingDiagnosticsStore(
        new MessagingDiagnosticsSnapshot(
            true,
            new OutboxDiagnosticsSnapshot(
                4,
                2,
                1,
                1,
                2,
                3,
                new DateTimeOffset(2026, 03, 27, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 03, 27, 10, 15, 0, TimeSpan.Zero),
                [
                    new OutboxMessageTypeDiagnosticsSnapshot("LawWatcher.IntegrationApi.Contracts.ReplayRequestedIntegrationEvent", 2, 1, 1, 0, 1, 1),
                    new OutboxMessageTypeDiagnosticsSnapshot("LawWatcher.IntegrationApi.Contracts.BackfillRequestedIntegrationEvent", 2, 1, 0, 1, 1, 3)
                ]),
            new InboxDiagnosticsSnapshot(
                3,
                [
                    new InboxConsumerDiagnosticsSnapshot("worker-lite.replay-requested", 2, new DateTimeOffset(2026, 03, 27, 10, 5, 0, TimeSpan.Zero)),
                    new InboxConsumerDiagnosticsSnapshot("worker-lite.backfill-requested", 1, new DateTimeOffset(2026, 03, 27, 10, 7, 0, TimeSpan.Zero))
                ]))),
    sqlOutboxEnabled: true,
    brokerEnabled: true);
var messagingDiagnostics = await messagingDiagnosticsQueryService.GetDiagnosticsAsync(CancellationToken.None);

Expect.Equal("rabbitmq", messagingDiagnostics.DeliveryMode, "Messaging diagnostics query service should report RabbitMQ as the primary delivery mode when broker transport is configured.", failures);
Expect.Equal("fallback", messagingDiagnostics.PollerMode, "Messaging diagnostics query service should describe SQL polling as fallback-only in broker mode.", failures);
Expect.True(messagingDiagnostics.DiagnosticsAvailable, "Messaging diagnostics query service should preserve store availability in the response contract.", failures);
Expect.Equal(1, messagingDiagnostics.Outbox.DeferredCount, "Messaging diagnostics query service should map deferred outbox counts into the response contract.", failures);
Expect.Equal(2, messagingDiagnostics.Outbox.MessageTypes.Count, "Messaging diagnostics query service should expose grouped per-message-type outbox diagnostics.", failures);
Expect.Equal("worker-lite.replay-requested", messagingDiagnostics.Inbox.Consumers.First().ConsumerName, "Messaging diagnostics query service should preserve inbox consumer names in the response contract.", failures);

var retentionMaintenanceCommandService = new RetentionMaintenanceCommandService(
    new StubRetentionMaintenanceStore(
        new RetentionMaintenanceExecutionResult(
            true,
            new DateTimeOffset(2026, 03, 27, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 03, 20, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 03, 13, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 02, 25, 11, 0, 0, TimeSpan.Zero),
            7,
            11,
            5,
            new DateTimeOffset(2026, 02, 27, 11, 0, 0, TimeSpan.Zero),
            9,
            true,
            "search_documents older than the requested retention window were pruned by indexed_at_utc.")));
var retentionMaintenance = await retentionMaintenanceCommandService.RunAsync(
    new RunRetentionMaintenanceCommand(
        168,
        336,
        720,
        672)
    {
        RequestedAtUtc = new DateTimeOffset(2026, 03, 27, 11, 0, 0, TimeSpan.Zero)
    },
    CancellationToken.None);

Expect.True(retentionMaintenance.MaintenanceAvailable, "Retention maintenance command service should preserve store availability in the response contract.", failures);
Expect.Equal(7, retentionMaintenance.DeletedPublishedOutboxCount, "Retention maintenance command service should map deleted published outbox rows into the response contract.", failures);
Expect.Equal(11, retentionMaintenance.DeletedProcessedInboxCount, "Retention maintenance command service should map deleted processed inbox rows into the response contract.", failures);
Expect.Equal(5, retentionMaintenance.DeletedEventFeedCount, "Retention maintenance command service should map deleted event-feed rows into the response contract.", failures);
Expect.Equal(new DateTimeOffset(2026, 02, 27, 11, 0, 0, TimeSpan.Zero), retentionMaintenance.SearchDocumentsCutoffUtc ?? DateTimeOffset.MinValue, "Retention maintenance command service should map the search-document retention cutoff into the response contract.", failures);
Expect.Equal(9, retentionMaintenance.DeletedSearchDocumentsCount, "Retention maintenance command service should map deleted search-document rows into the response contract.", failures);
Expect.True(retentionMaintenance.SearchDocumentsRetentionApplied, "Retention maintenance command service should report when search-document retention was applied.", failures);
Expect.Equal("search_documents older than the requested retention window were pruned by indexed_at_utc.", retentionMaintenance.SearchDocumentsRetentionReason, "Retention maintenance command service should preserve the explicit search-document retention reason.", failures);

var ollamaLlmHandler = new StubSequenceHttpMessageHandler(request =>
{
    var requestBody = request.Content is null
        ? string.Empty
        : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();

    if (request.Method != HttpMethod.Post ||
        request.RequestUri?.AbsolutePath != "/api/generate" ||
        !requestBody.Contains("\"model\":\"llama3.2:1b\"", StringComparison.Ordinal) ||
        !requestBody.Contains("\"stream\":false", StringComparison.Ordinal))
    {
        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"unexpected request\"}", Encoding.UTF8, "application/json")
        };
    }

    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {
              "model": "llama3.2:1b",
              "response": "Zmieniony akt wprowadza aktualizacje VAT i JPK.",
              "done": true
            }
            """, Encoding.UTF8, "application/json")
    };
});
var ollamaCompletion = await new OllamaLlmService(
    new HttpClient(ollamaLlmHandler)
    {
        BaseAddress = new Uri("http://127.0.0.1:11434")
    },
    "llama3.2:1b",
    "120s").CompleteAsync("Podsumuj zmiany VAT i JPK.", CancellationToken.None);

Expect.Equal("llama3.2:1b", ollamaCompletion.Model, "Ollama LLM adapter should preserve the runtime model identifier returned by the backend.", failures);
Expect.Equal("Zmieniony akt wprowadza aktualizacje VAT i JPK.", ollamaCompletion.Content, "Ollama LLM adapter should project the generated text from the backend response.", failures);
Expect.Equal(0, ollamaCompletion.Citations.Count, "Ollama LLM adapter should not invent citations when the backend response does not provide them.", failures);

var resolvedStateRoot = StateStoragePathResolver.ResolveRoot(
    new StateStorageOptions
    {
        StateRoot = Path.Combine("..", "..", "..", "artifacts", "state")
    },
    Path.Combine(
        Path.GetPathRoot(Directory.GetCurrentDirectory()) ?? Path.DirectorySeparatorChar.ToString(),
        "repos",
        "LawWatcher",
        "src",
        "Server",
        "LawWatcher.Api"));
var statePaths = LawWatcherStatePaths.ForRoot(resolvedStateRoot);
var expectedRepositoryRoot = Path.Combine(
    Path.GetPathRoot(Directory.GetCurrentDirectory()) ?? Path.DirectorySeparatorChar.ToString(),
    "repos",
    "LawWatcher");
var expectedStateRoot = Path.GetFullPath(Path.Combine(expectedRepositoryRoot, "artifacts", "state"));

Expect.Equal(
    expectedStateRoot,
    resolvedStateRoot,
    "State storage path resolver should normalize the shared root relative to the host content root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "ai-enrichment", "tasks"),
    statePaths.AiTasksRoot,
    "State storage paths should derive a stable AI task root from the shared state root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "integration-api", "replays"),
    statePaths.ReplaysRoot,
    "State storage paths should derive a stable replay root from the shared state root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "integration-api", "backfills"),
    statePaths.BackfillsRoot,
    "State storage paths should derive a stable backfill root from the shared state root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "taxonomy-and-profiles", "subscriptions"),
    statePaths.ProfileSubscriptionsRoot,
    "State storage paths should derive a stable subscription root from the shared state root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "integration-api", "webhook-registrations"),
    statePaths.WebhookRegistrationsRoot,
    "State storage paths should derive a stable webhook registration root from the shared state root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "notifications", "bill-alerts"),
    statePaths.BillAlertsRoot,
    "State storage paths should derive a stable bill alert root from the shared state root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "notifications", "dispatches"),
    statePaths.NotificationDispatchesRoot,
    "State storage paths should derive a stable notification dispatch root from the shared state root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "integration-api", "webhook-dispatches"),
    statePaths.WebhookEventDispatchesRoot,
    "State storage paths should derive a stable webhook event dispatch root from the shared state root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "taxonomy-and-profiles", "monitoring-profiles"),
    statePaths.MonitoringProfilesRoot,
    "State storage paths should derive a stable monitoring profile root from the shared state root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "legislative-intake", "bills"),
    statePaths.BillsRoot,
    "State storage paths should derive a stable imported bill root from the shared state root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "legislative-process", "processes"),
    statePaths.ProcessesRoot,
    "State storage paths should derive a stable legislative process root from the shared state root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "legal-corpus", "acts"),
    statePaths.ActsRoot,
    "State storage paths should derive a stable published act root from the shared state root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "identity-and-access", "api-clients"),
    statePaths.ApiClientsRoot,
    "State storage paths should derive a stable API client root from the shared state root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "identity-and-access", "operator-accounts"),
    statePaths.OperatorAccountsRoot,
    "State storage paths should derive a stable operator account root from the shared state root.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "search", "documents"),
    statePaths.SearchIndexRoot,
    "State storage paths should derive a stable search index root from the shared state root.",
    failures);

var objectStorageOptions = new ObjectStorageOptions
{
    LocalDocumentsRoot = Path.Combine("..", "..", "artifacts", "documents"),
    Minio = new S3CompatibleDocumentStoreOptions
    {
        Endpoint = "http://minio:9000",
        AccessKey = "lawwatcher",
        SecretKey = "ChangeMe!123456"
    }
};
Expect.Equal(
    DocumentStoreBackend.S3Compatible,
    DocumentStoreRuntimeResolver.Select(objectStorageOptions),
    "Document store runtime resolver should choose the S3-compatible backend when MinIO credentials are configured.",
    failures);
Expect.Equal(
    Path.GetFullPath(Path.Combine(expectedRepositoryRoot, "src", "artifacts", "documents")),
    DocumentStoreRuntimeResolver.ResolveLocalDocumentsRoot(
        objectStorageOptions,
        Path.Combine(expectedRepositoryRoot, "src", "Server", "LawWatcher.Api")),
    "Document store runtime resolver should normalize the local fallback root relative to the host content root.",
    failures);
Expect.False(
    new S3CompatibleDocumentStoreOptions
    {
        Endpoint = "http://minio:9000",
        AccessKey = "lawwatcher",
        SecretKey = ""
    }.IsConfigured(),
    "S3-compatible document store options should remain disabled until endpoint, access key and secret key are all present.",
    failures);
Expect.Equal(
    Path.Combine(expectedStateRoot, "integration-api", "events"),
    statePaths.EventFeedRoot,
    "State storage paths should derive a stable event feed root from the shared state root.",
    failures);

var createdAt = new DateTimeOffset(2026, 03, 25, 10, 00, 00, TimeSpan.Zero);
var monitoringProfileId = new MonitoringProfileId(Guid.Parse("D9A3E76F-739D-42AD-9A25-4FA51E627E21"));
var monitoringProfile = MonitoringProfile.Create(
    monitoringProfileId,
    "Podatki CIT",
    AlertPolicy.Immediate(),
    createdAt);

Expect.Equal(monitoringProfileId, monitoringProfile.Id, "Monitoring profile should preserve the supplied identifier.", failures);
Expect.Equal("Podatki CIT", monitoringProfile.Name, "Monitoring profile should expose the created name.", failures);
Expect.Equal("immediate", monitoringProfile.AlertPolicy.Code, "Monitoring profile should keep the selected alert policy.", failures);
Expect.Equal(1L, monitoringProfile.Version, "Monitoring profile creation should append one domain event.", failures);

monitoringProfile.AddRule(ProfileRule.Keyword("CIT"), createdAt.AddMinutes(5));
monitoringProfile.AddRule(ProfileRule.Keyword("CIT"), createdAt.AddMinutes(6));
monitoringProfile.ChangeAlertPolicy(AlertPolicy.Digest(TimeSpan.FromHours(6)), createdAt.AddMinutes(10));
monitoringProfile.Deactivate(createdAt.AddMinutes(15));

var inactiveMonitoringProfileRejectedRuleChange = false;
try
{
    monitoringProfile.AddRule(ProfileRule.Keyword("VAT"), createdAt.AddMinutes(16));
}
catch (InvalidOperationException)
{
    inactiveMonitoringProfileRejectedRuleChange = true;
}

Expect.Equal(1, monitoringProfile.Rules.Count, "Monitoring profile should deduplicate identical keyword rules.", failures);
Expect.Equal("digest", monitoringProfile.AlertPolicy.Code, "Monitoring profile should switch to digest policy through a domain method.", failures);
Expect.False(monitoringProfile.IsActive, "Monitoring profile should become inactive after deactivation.", failures);
Expect.Equal(4L, monitoringProfile.Version, "Monitoring profile should only emit events for effective state changes, including deactivation.", failures);
Expect.Equal(4, monitoringProfile.UncommittedEvents.Count, "Monitoring profile should accumulate created, rule-added, policy-changed and deactivated events.", failures);
Expect.True(inactiveMonitoringProfileRejectedRuleChange, "Monitoring profile should reject further rule changes after deactivation.", failures);

var queryService = new MonitoringProfilesQueryService(new StubMonitoringProfileReadRepository(
    new MonitoringProfileReadModel(Guid.Parse("1E54137F-4CEC-4D76-B7E0-3E0571B0A8A4"), "VAT", "digest", ["VAT", "JPK"]),
    new MonitoringProfileReadModel(Guid.Parse("0FCE8E11-C7FB-4A1A-A59E-5CD2DB39FB29"), "CIT", "immediate", ["CIT"])));

var profiles = await queryService.GetProfilesAsync(CancellationToken.None);

Expect.Equal(2, profiles.Count, "Monitoring profile query service should return the repository projection set.", failures);
Expect.Equal("CIT", profiles[0].Name, "Monitoring profile query service should sort profiles by name for a stable API response.", failures);
Expect.Equal("immediate", profiles[0].AlertPolicy, "Monitoring profile query service should preserve alert policy codes in the contract response.", failures);
Expect.SequenceEqual(["CIT"], profiles[0].Keywords, "Monitoring profile query service should project keywords into the API contract.", failures);

var durableStateRoot = Path.Combine(AppContext.BaseDirectory, "spec-artifacts", "durable-state", Guid.NewGuid().ToString("N"));

var durableMonitoringProfilesRoot = Path.Combine(durableStateRoot, "monitoring-profiles");
var durableMonitoringProfileRepository = new FileBackedMonitoringProfileRepository(durableMonitoringProfilesRoot);
var durableMonitoringProfileProjection = new FileBackedMonitoringProfileProjectionStore(durableMonitoringProfilesRoot);
var durableMonitoringProfileCommandService = new MonitoringProfilesCommandService(
    durableMonitoringProfileRepository,
    durableMonitoringProfileProjection);

var citDurableProfileId = Guid.Parse("D6A5D33F-09EF-4952-BE02-B87491B08E90");
await durableMonitoringProfileCommandService.CreateAsync(new CreateMonitoringProfileCommand(
    citDurableProfileId,
    "Podatki CIT",
    AlertPolicy.Immediate()), CancellationToken.None);
await durableMonitoringProfileCommandService.AddRuleAsync(new AddMonitoringProfileRuleCommand(
    citDurableProfileId,
    ProfileRule.Keyword("CIT")), CancellationToken.None);
await durableMonitoringProfileCommandService.AddRuleAsync(new AddMonitoringProfileRuleCommand(
    citDurableProfileId,
    ProfileRule.Keyword("estoński CIT")), CancellationToken.None);
await durableMonitoringProfileCommandService.AddRuleAsync(new AddMonitoringProfileRuleCommand(
    citDurableProfileId,
    ProfileRule.Keyword("CIT")), CancellationToken.None);

var vatDurableProfileId = Guid.Parse("D1BEA804-4345-4F2A-88B3-109FFDB0EC97");
await durableMonitoringProfileCommandService.CreateAsync(new CreateMonitoringProfileCommand(
    vatDurableProfileId,
    "VAT i JPK",
    AlertPolicy.Digest(TimeSpan.FromHours(12))), CancellationToken.None);
await durableMonitoringProfileCommandService.AddRuleAsync(new AddMonitoringProfileRuleCommand(
    vatDurableProfileId,
    ProfileRule.Keyword("VAT")), CancellationToken.None);
await durableMonitoringProfileCommandService.AddRuleAsync(new AddMonitoringProfileRuleCommand(
    vatDurableProfileId,
    ProfileRule.Keyword("JPK_V7")), CancellationToken.None);
await durableMonitoringProfileCommandService.DeactivateAsync(new DeactivateMonitoringProfileCommand(
    citDurableProfileId), CancellationToken.None);

var reloadedMonitoringProfilesQueryService = new MonitoringProfilesQueryService(
    new FileBackedMonitoringProfileProjectionStore(durableMonitoringProfilesRoot));
var durableProfiles = await reloadedMonitoringProfilesQueryService.GetProfilesAsync(CancellationToken.None);
var reloadedInactiveMonitoringProfile = await durableMonitoringProfileRepository.GetAsync(
    new MonitoringProfileId(citDurableProfileId),
    CancellationToken.None);

Expect.False(reloadedInactiveMonitoringProfile?.IsActive ?? true, "File-backed monitoring profile repository should preserve deactivation in the aggregate history across reload.", failures);
Expect.Equal(1, durableProfiles.Count, "File-backed monitoring profile projection should remove deactivated profiles from the active read model across store reload.", failures);
Expect.Equal("VAT i JPK", durableProfiles[0].Name, "File-backed monitoring profile query should expose only active profiles after reload.", failures);
#if false
Expect.SequenceEqual(["CIT", "estoński CIT"], durableProfiles[0].Keywords, "File-backed monitoring profile projection should persist keyword rules across reload.", failures);
Expect.Equal("digest", durableProfiles[1].AlertPolicy, "File-backed monitoring profile projection should preserve digest policy across reload.", failures);
#endif
Expect.SequenceEqual(["JPK_V7", "VAT"], durableProfiles[0].Keywords, "File-backed monitoring profile projection should preserve keyword rules for active profiles across reload.", failures);
Expect.Equal("digest", durableProfiles[0].AlertPolicy, "File-backed monitoring profile projection should preserve digest policy across reload.", failures);

var billRepository = new InMemoryImportedBillRepository();
var billProjection = new InMemoryImportedBillProjectionStore();
var billCommandService = new LegislativeIntakeCommandService(billRepository, billProjection);
var billsQueryService = new BillsQueryService(billProjection);

var registerBillCommand = new RegisterBillCommand(
    Guid.Parse("4A8D7B52-311F-4A3F-9AF0-0C0651946A0C"),
    "sejm",
    "X-123",
    "https://www.sejm.gov.pl/druk/X-123",
    "Ustawa o zmianie CIT",
    new DateOnly(2026, 03, 24));

await billCommandService.RegisterAsync(registerBillCommand, CancellationToken.None);
await billCommandService.AttachDocumentAsync(new AttachBillDocumentCommand(registerBillCommand.BillId, "draft", "bills/X-123/original.pdf"), CancellationToken.None);
await billCommandService.AttachDocumentAsync(new AttachBillDocumentCommand(registerBillCommand.BillId, "draft", "bills/X-123/original.pdf"), CancellationToken.None);
await billCommandService.RegisterAsync(new RegisterBillCommand(
    Guid.Parse("1282CCB9-B6B6-4A58-A1E4-1C563486BDA7"),
    "sejm",
    "X-200",
    "https://www.sejm.gov.pl/druk/X-200",
    "Ustawa o zmianie VAT",
    new DateOnly(2026, 03, 25)), CancellationToken.None);

var persistedBill = await billRepository.GetAsync(new BillId(registerBillCommand.BillId), CancellationToken.None);
var listedBills = await billsQueryService.GetBillsAsync(CancellationToken.None);

Expect.True(persistedBill is not null, "Legislative intake repository should rehydrate a saved bill aggregate from the event stream.", failures);
Expect.Equal("Ustawa o zmianie CIT", persistedBill?.Title ?? string.Empty, "Legislative intake repository should preserve the bill title.", failures);
Expect.Equal(1, persistedBill?.Documents.Count ?? 0, "Legislative intake aggregate should deduplicate identical attached documents.", failures);
Expect.Equal(2, listedBills.Count, "Bills query service should expose projected bills after commands complete.", failures);
Expect.Equal("Ustawa o zmianie VAT", listedBills[0].Title, "Bills query service should sort bills by most recent submission date.", failures);
Expect.Equal("X-123", listedBills[1].ExternalId, "Bills query service should preserve the external bill identifier in the contract response.", failures);
Expect.SequenceEqual(["draft"], listedBills[1].DocumentKinds, "Bills query service should expose projected document kinds.", failures);

var durableBillsRoot = Path.Combine(durableStateRoot, "bills");
var durableBillRepository = new FileBackedImportedBillRepository(durableBillsRoot);
var durableBillProjection = new FileBackedImportedBillProjectionStore(durableBillsRoot);
var durableBillCommandService = new LegislativeIntakeCommandService(durableBillRepository, durableBillProjection);

await durableBillCommandService.RegisterAsync(new RegisterBillCommand(
    Guid.Parse("4C478924-0090-417D-965F-6BC561F67BA7"),
    "sejm",
    "X-901",
    "https://www.sejm.gov.pl/druk/X-901",
    "Ustawa o zmianie akcyzy",
    new DateOnly(2026, 03, 26)), CancellationToken.None);
await durableBillCommandService.AttachDocumentAsync(new AttachBillDocumentCommand(
    Guid.Parse("4C478924-0090-417D-965F-6BC561F67BA7"),
    "draft",
    "bills/X-901/original.pdf"), CancellationToken.None);

var reloadedDurableBillRepository = new FileBackedImportedBillRepository(durableBillsRoot);
var reloadedDurableBillProjection = new FileBackedImportedBillProjectionStore(durableBillsRoot);
var durablePersistedBill = await reloadedDurableBillRepository.GetAsync(
    new BillId(Guid.Parse("4C478924-0090-417D-965F-6BC561F67BA7")),
    CancellationToken.None);
var durableListedBills = await new BillsQueryService(reloadedDurableBillProjection).GetBillsAsync(CancellationToken.None);

Expect.True(durablePersistedBill is not null, "File-backed bill repository should rehydrate a saved bill aggregate after store reload.", failures);
Expect.Equal(1, durablePersistedBill?.Documents.Count ?? 0, "File-backed bill aggregate should preserve attached documents after reload.", failures);
Expect.Equal(1, durableListedBills.Count, "File-backed bill projection should preserve the bill list across store reload.", failures);
Expect.Equal("X-901", durableListedBills[0].ExternalId, "File-backed bill projection should preserve external identifiers across reload.", failures);
Expect.SequenceEqual(["draft"], durableListedBills[0].DocumentKinds, "File-backed bill projection should preserve document kinds across reload.", failures);

var processRepository = new InMemoryLegislativeProcessRepository();
var processProjection = new InMemoryLegislativeProcessProjectionStore();
var processCommandService = new LegislativeProcessCommandService(processRepository, processProjection);
var processesQueryService = new ProcessesQueryService(processProjection);

await processCommandService.StartAsync(new StartLegislativeProcessCommand(
    Guid.Parse("22ADAC16-41CB-4B5D-BAAF-E6A2D43B5693"),
    listedBills[1].Id,
    listedBills[1].Title,
    listedBills[1].ExternalId,
    LegislativeStage.Submitted(new DateOnly(2026, 03, 24))), CancellationToken.None);
await processCommandService.RecordStageAsync(new RecordLegislativeStageCommand(
    Guid.Parse("22ADAC16-41CB-4B5D-BAAF-E6A2D43B5693"),
    LegislativeStage.Of("first-reading", "First reading", new DateOnly(2026, 03, 26))), CancellationToken.None);
await processCommandService.RecordStageAsync(new RecordLegislativeStageCommand(
    Guid.Parse("22ADAC16-41CB-4B5D-BAAF-E6A2D43B5693"),
    LegislativeStage.Of("first-reading", "First reading", new DateOnly(2026, 03, 26))), CancellationToken.None);
await processCommandService.StartAsync(new StartLegislativeProcessCommand(
    Guid.Parse("16A896B8-07F1-4210-8557-23F7451DFD13"),
    listedBills[0].Id,
    listedBills[0].Title,
    listedBills[0].ExternalId,
    LegislativeStage.Submitted(new DateOnly(2026, 03, 25))), CancellationToken.None);

var persistedProcess = await processRepository.GetAsync(new LegislativeProcessId(Guid.Parse("22ADAC16-41CB-4B5D-BAAF-E6A2D43B5693")), CancellationToken.None);
var listedProcesses = await processesQueryService.GetProcessesAsync(CancellationToken.None);

Expect.True(persistedProcess is not null, "Legislative process repository should rehydrate a saved process aggregate from the event stream.", failures);
Expect.Equal("first-reading", persistedProcess?.CurrentStage.Code ?? string.Empty, "Legislative process aggregate should update the current stage through domain methods.", failures);
Expect.Equal(2, persistedProcess?.Stages.Count ?? 0, "Legislative process aggregate should deduplicate identical stage transitions.", failures);
Expect.Equal(2, listedProcesses.Count, "Processes query service should expose projected processes after commands complete.", failures);
Expect.Equal("first-reading", listedProcesses[0].CurrentStageCode, "Processes query service should sort processes by most recent stage date.", failures);
Expect.Equal("X-123", listedProcesses[0].BillExternalId, "Processes query service should preserve the linked bill external identifier.", failures);
Expect.Equal(2, listedProcesses[0].StageCount, "Processes query service should expose the number of recorded stages.", failures);

var durableProcessesRoot = Path.Combine(durableStateRoot, "processes");
var durableProcessRepository = new FileBackedLegislativeProcessRepository(durableProcessesRoot);
var durableProcessProjection = new FileBackedLegislativeProcessProjectionStore(durableProcessesRoot);
var durableProcessCommandService = new LegislativeProcessCommandService(durableProcessRepository, durableProcessProjection);

await durableProcessCommandService.StartAsync(new StartLegislativeProcessCommand(
    Guid.Parse("EED43773-4AA8-4D25-B56F-6C290EA68F0A"),
    durableListedBills[0].Id,
    durableListedBills[0].Title,
    durableListedBills[0].ExternalId,
    LegislativeStage.Submitted(new DateOnly(2026, 03, 26))), CancellationToken.None);
await durableProcessCommandService.RecordStageAsync(new RecordLegislativeStageCommand(
    Guid.Parse("EED43773-4AA8-4D25-B56F-6C290EA68F0A"),
    LegislativeStage.Of("committee", "Committee", new DateOnly(2026, 03, 27))), CancellationToken.None);

var durablePersistedProcess = await new FileBackedLegislativeProcessRepository(durableProcessesRoot).GetAsync(
    new LegislativeProcessId(Guid.Parse("EED43773-4AA8-4D25-B56F-6C290EA68F0A")),
    CancellationToken.None);
var durableListedProcesses = await new ProcessesQueryService(new FileBackedLegislativeProcessProjectionStore(durableProcessesRoot)).GetProcessesAsync(CancellationToken.None);

Expect.True(durablePersistedProcess is not null, "File-backed legislative process repository should rehydrate a saved process aggregate after store reload.", failures);
Expect.Equal("committee", durablePersistedProcess?.CurrentStage.Code ?? string.Empty, "File-backed legislative process aggregate should preserve the latest stage after reload.", failures);
Expect.Equal(1, durableListedProcesses.Count, "File-backed legislative process projection should preserve the process list across store reload.", failures);
Expect.Equal("committee", durableListedProcesses[0].CurrentStageCode, "File-backed legislative process projection should preserve current stage code across reload.", failures);
Expect.Equal(2, durableListedProcesses[0].StageCount, "File-backed legislative process projection should preserve stage history across reload.", failures);

var actRepository = new InMemoryPublishedActRepository();
var actProjection = new InMemoryPublishedActProjectionStore();
var legalCorpusCommandService = new LegalCorpusCommandService(actRepository, actProjection);
var actsQueryService = new ActsQueryService(actProjection);

await legalCorpusCommandService.RegisterAsync(new RegisterActCommand(
    Guid.Parse("4923A708-2B89-4F17-93A9-0AA3A59C2056"),
    listedBills[1].Id,
    listedBills[1].Title,
    listedBills[1].ExternalId,
    "https://eli.gov.pl/eli/DU/2026/501/ogl",
    "Ustawa z dnia 28 marca 2026 r. o zmianie ustawy o CIT",
    new DateOnly(2026, 03, 28),
    new DateOnly(2026, 04, 01)), CancellationToken.None);
await legalCorpusCommandService.AttachArtifactAsync(new AttachActArtifactCommand(
    Guid.Parse("4923A708-2B89-4F17-93A9-0AA3A59C2056"),
    "text",
    "acts/DU/2026/501/text.pdf"), CancellationToken.None);
await legalCorpusCommandService.AttachArtifactAsync(new AttachActArtifactCommand(
    Guid.Parse("4923A708-2B89-4F17-93A9-0AA3A59C2056"),
    "text",
    "acts/DU/2026/501/text.pdf"), CancellationToken.None);
await legalCorpusCommandService.RegisterAsync(new RegisterActCommand(
    Guid.Parse("9021345F-3220-498C-9D50-3044F27E149F"),
    listedBills[0].Id,
    listedBills[0].Title,
    listedBills[0].ExternalId,
    "https://eli.gov.pl/eli/DU/2026/502/ogl",
    "Ustawa z dnia 29 marca 2026 r. o zmianie ustawy o VAT",
    new DateOnly(2026, 03, 29),
    new DateOnly(2026, 04, 05)), CancellationToken.None);

var persistedAct = await actRepository.GetAsync(new ActId(Guid.Parse("4923A708-2B89-4F17-93A9-0AA3A59C2056")), CancellationToken.None);
var listedActs = await actsQueryService.GetActsAsync(CancellationToken.None);

Expect.True(persistedAct is not null, "Legal corpus repository should rehydrate a saved published act aggregate from the event stream.", failures);
Expect.Equal("https://eli.gov.pl/eli/DU/2026/501/ogl", persistedAct?.Eli.Value ?? string.Empty, "Published act aggregate should preserve the imported ELI reference.", failures);
Expect.Equal(1, persistedAct?.Artifacts.Count ?? 0, "Published act aggregate should deduplicate identical artifacts.", failures);
Expect.Equal(2, listedActs.Count, "Acts query service should expose projected acts after commands complete.", failures);
Expect.Equal("X-200", listedActs[0].BillExternalId, "Acts query service should sort acts by most recent publication date.", failures);
Expect.Equal(new DateOnly(2026, 04, 01), listedActs[1].EffectiveFrom ?? DateOnly.MinValue, "Acts query service should preserve the effective date in the contract response.", failures);
Expect.SequenceEqual(["text"], listedActs[1].ArtifactKinds, "Acts query service should project artifact kinds into the API contract.", failures);

var durableActsRoot = Path.Combine(durableStateRoot, "acts");
var durableActRepository = new FileBackedPublishedActRepository(durableActsRoot);
var durableActProjection = new FileBackedPublishedActProjectionStore(durableActsRoot);
var durableActCommandService = new LegalCorpusCommandService(durableActRepository, durableActProjection);

await durableActCommandService.RegisterAsync(new RegisterActCommand(
    Guid.Parse("F59A4FCA-B739-49F7-B3D9-B91D3E96D824"),
    durableListedBills[0].Id,
    durableListedBills[0].Title,
    durableListedBills[0].ExternalId,
    "https://eli.gov.pl/eli/DU/2026/777/ogl",
    "Ustawa z dnia 30 marca 2026 r. o zmianie akcyzy",
    new DateOnly(2026, 03, 30),
    new DateOnly(2026, 04, 10)), CancellationToken.None);
await durableActCommandService.AttachArtifactAsync(new AttachActArtifactCommand(
    Guid.Parse("F59A4FCA-B739-49F7-B3D9-B91D3E96D824"),
    "text",
    "acts/DU/2026/777/text.pdf"), CancellationToken.None);

var durablePersistedAct = await new FileBackedPublishedActRepository(durableActsRoot).GetAsync(
    new ActId(Guid.Parse("F59A4FCA-B739-49F7-B3D9-B91D3E96D824")),
    CancellationToken.None);
var durableListedActs = await new ActsQueryService(new FileBackedPublishedActProjectionStore(durableActsRoot)).GetActsAsync(CancellationToken.None);

Expect.True(durablePersistedAct is not null, "File-backed published act repository should rehydrate a saved act aggregate after store reload.", failures);
Expect.Equal(1, durablePersistedAct?.Artifacts.Count ?? 0, "File-backed published act aggregate should preserve attached artifacts after reload.", failures);
Expect.Equal(1, durableListedActs.Count, "File-backed published act projection should preserve the act list across store reload.", failures);
Expect.Equal("https://eli.gov.pl/eli/DU/2026/777/ogl", durableListedActs[0].Eli, "File-backed published act projection should preserve ELI across reload.", failures);
Expect.SequenceEqual(["text"], durableListedActs[0].ArtifactKinds, "File-backed published act projection should preserve artifact kinds across reload.", failures);

var subscriptionRepository = new InMemoryProfileSubscriptionRepository();
var subscriptionProjection = new InMemoryProfileSubscriptionProjectionStore();
var subscriptionCommandService = new ProfileSubscriptionsCommandService(subscriptionRepository, subscriptionProjection);
var subscriptionsQueryService = new ProfileSubscriptionsQueryService(subscriptionProjection);

await subscriptionCommandService.CreateAsync(new CreateProfileSubscriptionCommand(
    Guid.Parse("6ED3611A-1527-4C30-851A-2E8B33161B79"),
    profiles[0].Id,
    profiles[0].Name,
    "anna.nowak@example.test",
    SubscriptionChannel.Email(),
    AlertPolicy.Immediate()), CancellationToken.None);
await subscriptionCommandService.ChangeAlertPolicyAsync(new ChangeProfileSubscriptionAlertPolicyCommand(
    Guid.Parse("6ED3611A-1527-4C30-851A-2E8B33161B79"),
    AlertPolicy.Digest(TimeSpan.FromHours(8))), CancellationToken.None);
await subscriptionCommandService.ChangeAlertPolicyAsync(new ChangeProfileSubscriptionAlertPolicyCommand(
    Guid.Parse("6ED3611A-1527-4C30-851A-2E8B33161B79"),
    AlertPolicy.Digest(TimeSpan.FromHours(8))), CancellationToken.None);
await subscriptionCommandService.CreateAsync(new CreateProfileSubscriptionCommand(
    Guid.Parse("534E11E8-261D-46E4-9878-6DA792E44154"),
    profiles[1].Id,
    profiles[1].Name,
    "marek.kowalski@example.test",
    SubscriptionChannel.Email(),
    AlertPolicy.Digest(TimeSpan.FromHours(12))), CancellationToken.None);

var persistedSubscription = await subscriptionRepository.GetAsync(
    new ProfileSubscriptionId(Guid.Parse("6ED3611A-1527-4C30-851A-2E8B33161B79")),
    CancellationToken.None);
var listedSubscriptions = await subscriptionsQueryService.GetSubscriptionsAsync(CancellationToken.None);

Expect.True(persistedSubscription is not null, "Profile subscription repository should rehydrate a saved subscription aggregate from the event stream.", failures);
Expect.Equal("digest", persistedSubscription?.AlertPolicy.Code ?? string.Empty, "Profile subscription aggregate should update alert policy through domain methods.", failures);
Expect.Equal(2L, persistedSubscription?.Version ?? 0L, "Profile subscription aggregate should emit events only for effective policy changes.", failures);
Expect.Equal(2, listedSubscriptions.Count, "Subscriptions query service should expose projected subscriptions after commands complete.", failures);
Expect.Equal("anna.nowak@example.test", listedSubscriptions[0].Subscriber, "Subscriptions query service should sort subscriptions by subscriber for a stable API response.", failures);
Expect.Equal("digest", listedSubscriptions[0].AlertPolicy, "Subscriptions query service should preserve subscription alert policy in the contract response.", failures);
Expect.Equal("email", listedSubscriptions[0].Channel, "Subscriptions query service should expose the delivery channel code.", failures);

await subscriptionCommandService.DeactivateAsync(new DeactivateProfileSubscriptionCommand(
    Guid.Parse("6ED3611A-1527-4C30-851A-2E8B33161B79")), CancellationToken.None);

var deactivatedSubscription = await subscriptionRepository.GetAsync(
    new ProfileSubscriptionId(Guid.Parse("6ED3611A-1527-4C30-851A-2E8B33161B79")),
    CancellationToken.None);
var activeSubscriptionsAfterDeactivate = await subscriptionsQueryService.GetSubscriptionsAsync(CancellationToken.None);

Expect.False(deactivatedSubscription?.IsActive ?? true, "Profile subscription aggregate should become inactive after deactivation.", failures);
Expect.Equal(1, activeSubscriptionsAfterDeactivate.Count, "Subscriptions query service should remove deactivated subscriptions from the active projection.", failures);
Expect.Equal("marek.kowalski@example.test", activeSubscriptionsAfterDeactivate[0].Subscriber, "Subscriptions query service should only list active subscriptions after deactivation.", failures);

var webhookRepository = new InMemoryWebhookRegistrationRepository();
var webhookProjection = new InMemoryWebhookRegistrationProjectionStore();
var webhookCommandService = new WebhookRegistrationsCommandService(webhookRepository, webhookProjection);
var webhooksQueryService = new WebhookRegistrationsQueryService(webhookProjection);

await webhookCommandService.RegisterAsync(new RegisterWebhookCommand(
    Guid.Parse("3B3A2A38-A566-42AB-861B-B7EF909F9840"),
    "ERP sync",
    "https://erp.example.test/lawwatcher",
    ["alert.created", "alert.created", "process.updated"]), CancellationToken.None);
await webhookCommandService.UpdateAsync(new UpdateWebhookCommand(
    Guid.Parse("3B3A2A38-A566-42AB-861B-B7EF909F9840"),
    "ERP sync v2",
    "https://erp.example.test/lawwatcher/v2",
    ["process.updated", "bill.imported", "alert.created", "bill.imported"]), CancellationToken.None);
await webhookCommandService.DeactivateAsync(new DeactivateWebhookCommand(
    Guid.Parse("3B3A2A38-A566-42AB-861B-B7EF909F9840")), CancellationToken.None);
await webhookCommandService.DeactivateAsync(new DeactivateWebhookCommand(
    Guid.Parse("3B3A2A38-A566-42AB-861B-B7EF909F9840")), CancellationToken.None);
await webhookCommandService.RegisterAsync(new RegisterWebhookCommand(
    Guid.Parse("44EAF632-1CEA-4B8F-B683-B6F860152D04"),
    "Portal audit",
    "https://audit.example.test/webhooks/lawwatcher",
    ["bill.imported"]), CancellationToken.None);

var persistedWebhook = await webhookRepository.GetAsync(
    new WebhookRegistrationId(Guid.Parse("3B3A2A38-A566-42AB-861B-B7EF909F9840")),
    CancellationToken.None);
var listedWebhooks = await webhooksQueryService.GetWebhooksAsync(CancellationToken.None);

Expect.True(persistedWebhook is not null, "Webhook repository should rehydrate a saved webhook registration aggregate from the event stream.", failures);
Expect.Equal(false, persistedWebhook?.IsActive ?? true, "Webhook aggregate should support explicit deactivation through domain methods.", failures);
Expect.Equal(3, persistedWebhook?.EventTypes.Count ?? 0, "Webhook aggregate should preserve the latest deduplicated subscribed event types after update.", failures);
Expect.Equal(3L, persistedWebhook?.Version ?? 0L, "Webhook aggregate should emit events only for effective lifecycle changes.", failures);
Expect.Equal(2, listedWebhooks.Count, "Webhook query service should expose projected registrations after commands complete.", failures);
Expect.Equal("ERP sync v2", listedWebhooks[0].Name, "Webhook query service should expose the updated webhook name in the projection response.", failures);
Expect.Equal("https://erp.example.test/lawwatcher/v2", listedWebhooks[0].CallbackUrl, "Webhook query service should expose the updated callback URL in the projection response.", failures);
Expect.Equal(false, listedWebhooks[0].IsActive, "Webhook query service should expose deactivated registrations.", failures);
Expect.SequenceEqual(["alert.created", "bill.imported", "process.updated"], listedWebhooks[0].EventTypes, "Webhook query service should preserve updated deduplicated event types in the contract response.", failures);

var replayRepository = new InMemoryReplayRequestRepository();
var replayProjection = new InMemoryReplayRequestProjectionStore();
var replayCommandService = new ReplayRequestsCommandService(replayRepository, replayProjection);
var replaysQueryService = new ReplayRequestsQueryService(replayProjection);

await replayCommandService.RequestAsync(new RequestReplayCommand(
    Guid.Parse("4DEFC0EA-9CA8-4C1E-A47A-B133F7D6FA72"),
    ReplayScope.Of("search-index"),
    "system"), CancellationToken.None);
await replayCommandService.MarkStartedAsync(new MarkReplayStartedCommand(
    Guid.Parse("4DEFC0EA-9CA8-4C1E-A47A-B133F7D6FA72")), CancellationToken.None);
await replayCommandService.MarkCompletedAsync(new MarkReplayCompletedCommand(
    Guid.Parse("4DEFC0EA-9CA8-4C1E-A47A-B133F7D6FA72")), CancellationToken.None);
await replayCommandService.MarkCompletedAsync(new MarkReplayCompletedCommand(
    Guid.Parse("4DEFC0EA-9CA8-4C1E-A47A-B133F7D6FA72")), CancellationToken.None);
await replayCommandService.RequestAsync(new RequestReplayCommand(
    Guid.Parse("83CCF879-BB2D-4D20-B34B-A6458A7AE2A8"),
    ReplayScope.Of("sql-projections"),
    "admin"), CancellationToken.None);

var persistedReplay = await replayRepository.GetAsync(
    new ReplayRequestId(Guid.Parse("4DEFC0EA-9CA8-4C1E-A47A-B133F7D6FA72")),
    CancellationToken.None);
var listedReplays = await replaysQueryService.GetReplaysAsync(CancellationToken.None);
var completedReplay = listedReplays.Single(replay => replay.Scope == "search-index");
var queuedReplay = listedReplays.Single(replay => replay.Scope == "sql-projections");

Expect.True(persistedReplay is not null, "Replay repository should rehydrate a saved replay request aggregate from the event stream.", failures);
Expect.Equal("completed", persistedReplay?.Status.Code ?? string.Empty, "Replay aggregate should transition through running into completed state.", failures);
Expect.Equal(3L, persistedReplay?.Version ?? 0L, "Replay aggregate should emit events only for effective lifecycle changes.", failures);
Expect.Equal(2, listedReplays.Count, "Replay query service should expose projected replay requests after commands complete.", failures);
Expect.Equal("completed", completedReplay.Status, "Replay query service should expose completed requests.", failures);
Expect.Equal("system", completedReplay.RequestedBy, "Replay query service should preserve the requester identity.", failures);
Expect.Equal("queued", queuedReplay.Status, "Replay query service should keep newly requested replays queued until work starts.", failures);

var replayExecutionRepository = new InMemoryReplayRequestRepository();
var replayExecutionProjection = new InMemoryReplayRequestProjectionStore();
var replayExecutionCommandService = new ReplayRequestsCommandService(replayExecutionRepository, replayExecutionProjection);
var replayExecutionService = new ReplayExecutionService(replayExecutionRepository, replayExecutionProjection);
var replayQueueProcessor = new ReplayQueueProcessor(replayExecutionRepository, replayExecutionService);
var replayExecutionQueryService = new ReplayRequestsQueryService(replayExecutionProjection);

await replayExecutionCommandService.RequestAsync(new RequestReplayCommand(
    Guid.Parse("93CA7B32-42E6-430D-95A8-B6859341A042"),
    ReplayScope.Of("search-index"),
    "runtime"), CancellationToken.None);
await replayExecutionCommandService.RequestAsync(new RequestReplayCommand(
    Guid.Parse("E7FCE1B0-A711-4E2B-ACFE-600E8C9591B0"),
    ReplayScope.Of("sql-projections"),
    "runtime"), CancellationToken.None);

var replayBatchResult = await replayQueueProcessor.ProcessAvailableAsync(maxTasks: 2, CancellationToken.None);
var replayExecutionResults = await replayExecutionQueryService.GetReplaysAsync(CancellationToken.None);

Expect.Equal(2, replayBatchResult.ProcessedCount, "Replay queue processor should process queued replay requests up to the requested batch size.", failures);
Expect.False(replayBatchResult.HasRemainingQueuedRequests, "Replay queue processor should report no remaining queued replay requests when the batch drained the queue.", failures);
Expect.True(replayExecutionResults.All(replay => replay.Status == "completed"), "Replay queue processor should eventually complete processed replay requests.", failures);

var brokeredReplayRepository = new InMemoryReplayRequestRepository();
var brokeredReplayProjection = new InMemoryReplayRequestProjectionStore();
var brokeredReplayCommandService = new ReplayRequestsCommandService(brokeredReplayRepository, brokeredReplayProjection);
var brokeredReplayExecutionService = new ReplayExecutionService(brokeredReplayRepository, brokeredReplayProjection);
var replayBrokeredOutboxStore = new InMemoryOutboxMessageStore();
var replayBrokeredPublisher = new RecordingIntegrationEventPublisher();
var replayBrokeredInboxStore = new InMemoryInboxStore();
var replayOutboxPublisher = new ReplayRequestedOutboxPublisher(replayBrokeredOutboxStore, replayBrokeredPublisher);
var replayMessageHandler = new ReplayRequestedMessageHandler(brokeredReplayExecutionService, replayBrokeredInboxStore);

await brokeredReplayCommandService.RequestAsync(new RequestReplayCommand(
    Guid.Parse("A778A619-CF07-431B-BF9D-00E12A0054D0"),
    ReplayScope.Of("broker-search-index"),
    "broker-runtime"), CancellationToken.None);
await replayBrokeredOutboxStore.EnqueueAsync(new ReplayRequestedIntegrationEvent(
    Guid.Parse("0F1758BA-F4E0-4090-89D1-C0A987A65D82"),
    DateTimeOffset.UtcNow,
    Guid.Parse("A778A619-CF07-431B-BF9D-00E12A0054D0"),
    "broker-search-index",
    "broker-runtime"), CancellationToken.None);

var replayOutboxPublishResult = await replayOutboxPublisher.PublishPendingAsync(maxMessages: 1, CancellationToken.None);
var publishedReplayEvent = replayBrokeredPublisher.PublishedEvents.OfType<ReplayRequestedIntegrationEvent>().Single();
var firstReplayBrokerHandleResult = await replayMessageHandler.HandleAsync(publishedReplayEvent, CancellationToken.None);
var secondReplayBrokerHandleResult = await replayMessageHandler.HandleAsync(publishedReplayEvent, CancellationToken.None);
var brokeredReplayResults = await new ReplayRequestsQueryService(brokeredReplayProjection).GetReplaysAsync(CancellationToken.None);
var brokeredCompletedReplay = brokeredReplayResults.Single(replay => replay.Id == Guid.Parse("A778A619-CF07-431B-BF9D-00E12A0054D0"));

Expect.Equal(1, replayOutboxPublishResult.PublishedCount, "Replay outbox publisher should publish one pending replay integration event to the broker port.", failures);
Expect.False(replayOutboxPublishResult.HasRemainingMessages, "Replay outbox publisher should report no remaining replay integration events after publishing the pending batch.", failures);
Expect.Equal(1, replayBrokeredPublisher.PublishedEvents.Count, "Replay outbox publisher should pass the integration event to the broker port exactly once.", failures);
Expect.Equal(1, replayBrokeredOutboxStore.PublishedMessageIds.Count, "Replay outbox publisher should mark the published replay outbox message as published.", failures);
Expect.True(firstReplayBrokerHandleResult.HasProcessedRequest, "Replay broker handler should process a broker-delivered replay request when it has not been handled before.", failures);
Expect.Equal("completed", firstReplayBrokerHandleResult.Status ?? string.Empty, "Replay broker handler should complete the queued replay request through the execution service.", failures);
Expect.False(secondReplayBrokerHandleResult.HasProcessedRequest, "Replay broker handler should stay idempotent for already processed replay broker messages.", failures);
Expect.Equal(1, replayBrokeredInboxStore.ProcessedMessages.Count, "Replay broker handler should record inbox idempotency for processed replay broker messages.", failures);
Expect.Equal("completed", brokeredCompletedReplay.Status, "Replay broker handler should leave the replay request in the completed state after broker delivery.", failures);

var backfillRepository = new InMemoryBackfillRequestRepository();
var backfillProjection = new InMemoryBackfillRequestProjectionStore();
var backfillCommandService = new BackfillRequestsCommandService(backfillRepository, backfillProjection);
var backfillsQueryService = new BackfillRequestsQueryService(backfillProjection);

await backfillCommandService.RequestAsync(new RequestBackfillCommand(
    Guid.Parse("834F7F95-E83D-4917-9F3C-64C31AAB5AA5"),
    BackfillSource.Of("sejm"),
    BackfillScope.Of("current-term"),
    new DateOnly(2026, 01, 01),
    new DateOnly(2026, 03, 31),
    "admin"), CancellationToken.None);
await backfillCommandService.MarkStartedAsync(new MarkBackfillStartedCommand(
    Guid.Parse("834F7F95-E83D-4917-9F3C-64C31AAB5AA5")), CancellationToken.None);
await backfillCommandService.MarkCompletedAsync(new MarkBackfillCompletedCommand(
    Guid.Parse("834F7F95-E83D-4917-9F3C-64C31AAB5AA5")), CancellationToken.None);
await backfillCommandService.MarkCompletedAsync(new MarkBackfillCompletedCommand(
    Guid.Parse("834F7F95-E83D-4917-9F3C-64C31AAB5AA5")), CancellationToken.None);
await backfillCommandService.RequestAsync(new RequestBackfillCommand(
    Guid.Parse("333DE5B0-01C5-4959-9D6D-FD43BEEA7EF4"),
    BackfillSource.Of("eli"),
    BackfillScope.Of("acts-2026"),
    new DateOnly(2026, 01, 01),
    null,
    "system"), CancellationToken.None);

var persistedBackfill = await backfillRepository.GetAsync(
    new BackfillRequestId(Guid.Parse("834F7F95-E83D-4917-9F3C-64C31AAB5AA5")),
    CancellationToken.None);
var listedBackfills = await backfillsQueryService.GetBackfillsAsync(CancellationToken.None);
var completedBackfill = listedBackfills.Single(backfill => backfill.Source == "sejm");
var queuedBackfill = listedBackfills.Single(backfill => backfill.Source == "eli");

Expect.True(persistedBackfill is not null, "Backfill repository should rehydrate a saved backfill request aggregate from the event stream.", failures);
Expect.Equal("completed", persistedBackfill?.Status.Code ?? string.Empty, "Backfill aggregate should transition through running into completed state.", failures);
Expect.Equal(3L, persistedBackfill?.Version ?? 0L, "Backfill aggregate should emit events only for effective lifecycle changes.", failures);
Expect.Equal(2, listedBackfills.Count, "Backfill query service should expose projected backfill requests after commands complete.", failures);
Expect.Equal("completed", completedBackfill.Status, "Backfill query service should expose completed requests.", failures);
Expect.Equal(new DateOnly(2026, 03, 31), completedBackfill.RequestedTo ?? DateOnly.MinValue, "Backfill query service should preserve the requested end date.", failures);
Expect.Equal("queued", queuedBackfill.Status, "Backfill query service should keep newly requested backfills queued until work starts.", failures);

var backfillExecutionRepository = new InMemoryBackfillRequestRepository();
var backfillExecutionProjection = new InMemoryBackfillRequestProjectionStore();
var backfillExecutionCommandService = new BackfillRequestsCommandService(backfillExecutionRepository, backfillExecutionProjection);
var backfillExecutionService = new BackfillExecutionService(backfillExecutionRepository, backfillExecutionProjection);
var backfillQueueProcessor = new BackfillQueueProcessor(backfillExecutionRepository, backfillExecutionService);
var backfillExecutionQueryService = new BackfillRequestsQueryService(backfillExecutionProjection);

await backfillExecutionCommandService.RequestAsync(new RequestBackfillCommand(
    Guid.Parse("2F78D8AB-1579-4173-B831-D5DC48F4C349"),
    BackfillSource.Of("sejm"),
    BackfillScope.Of("current-term"),
    new DateOnly(2026, 01, 01),
    new DateOnly(2026, 03, 31),
    "runtime"), CancellationToken.None);
await backfillExecutionCommandService.RequestAsync(new RequestBackfillCommand(
    Guid.Parse("5C6A2285-5D93-4AB0-A4CF-A5F84C5F8714"),
    BackfillSource.Of("eli"),
    BackfillScope.Of("acts-2026"),
    new DateOnly(2026, 01, 01),
    null,
    "runtime"), CancellationToken.None);

var backfillBatchResult = await backfillQueueProcessor.ProcessAvailableAsync(maxTasks: 2, CancellationToken.None);
var backfillExecutionResults = await backfillExecutionQueryService.GetBackfillsAsync(CancellationToken.None);

Expect.Equal(2, backfillBatchResult.ProcessedCount, "Backfill queue processor should process queued backfill requests up to the requested batch size.", failures);
Expect.False(backfillBatchResult.HasRemainingQueuedRequests, "Backfill queue processor should report no remaining queued backfill requests when the batch drained the queue.", failures);
Expect.True(backfillExecutionResults.All(backfill => backfill.Status == "completed"), "Backfill queue processor should eventually complete processed backfill requests.", failures);

var brokeredBackfillRepository = new InMemoryBackfillRequestRepository();
var brokeredBackfillProjection = new InMemoryBackfillRequestProjectionStore();
var brokeredBackfillCommandService = new BackfillRequestsCommandService(brokeredBackfillRepository, brokeredBackfillProjection);
var brokeredBackfillExecutionService = new BackfillExecutionService(brokeredBackfillRepository, brokeredBackfillProjection);
var backfillBrokeredOutboxStore = new InMemoryOutboxMessageStore();
var backfillBrokeredPublisher = new RecordingIntegrationEventPublisher();
var backfillBrokeredInboxStore = new InMemoryInboxStore();
var backfillOutboxPublisher = new BackfillRequestedOutboxPublisher(backfillBrokeredOutboxStore, backfillBrokeredPublisher);
var backfillMessageHandler = new BackfillRequestedMessageHandler(brokeredBackfillExecutionService, backfillBrokeredInboxStore);

await brokeredBackfillCommandService.RequestAsync(new RequestBackfillCommand(
    Guid.Parse("4D4C4660-FFB6-42A2-85A9-299CFE922337"),
    BackfillSource.Of("sejm"),
    BackfillScope.Of("broker-current-term"),
    new DateOnly(2026, 01, 01),
    new DateOnly(2026, 03, 31),
    "broker-runtime"), CancellationToken.None);
await backfillBrokeredOutboxStore.EnqueueAsync(new BackfillRequestedIntegrationEvent(
    Guid.Parse("7F5730B4-73DA-4797-9208-BB89556F968E"),
    DateTimeOffset.UtcNow,
    Guid.Parse("4D4C4660-FFB6-42A2-85A9-299CFE922337"),
    "sejm",
    "broker-current-term",
    new DateOnly(2026, 01, 01),
    new DateOnly(2026, 03, 31),
    "broker-runtime"), CancellationToken.None);

var backfillOutboxPublishResult = await backfillOutboxPublisher.PublishPendingAsync(maxMessages: 1, CancellationToken.None);
var publishedBackfillEvent = backfillBrokeredPublisher.PublishedEvents.OfType<BackfillRequestedIntegrationEvent>().Single();
var firstBackfillBrokerHandleResult = await backfillMessageHandler.HandleAsync(publishedBackfillEvent, CancellationToken.None);
var secondBackfillBrokerHandleResult = await backfillMessageHandler.HandleAsync(publishedBackfillEvent, CancellationToken.None);
var brokeredBackfillResults = await new BackfillRequestsQueryService(brokeredBackfillProjection).GetBackfillsAsync(CancellationToken.None);
var brokeredCompletedBackfill = brokeredBackfillResults.Single(backfill => backfill.Id == Guid.Parse("4D4C4660-FFB6-42A2-85A9-299CFE922337"));

Expect.Equal(1, backfillOutboxPublishResult.PublishedCount, "Backfill outbox publisher should publish one pending backfill integration event to the broker port.", failures);
Expect.False(backfillOutboxPublishResult.HasRemainingMessages, "Backfill outbox publisher should report no remaining backfill integration events after publishing the pending batch.", failures);
Expect.Equal(1, backfillBrokeredPublisher.PublishedEvents.Count, "Backfill outbox publisher should pass the integration event to the broker port exactly once.", failures);
Expect.Equal(1, backfillBrokeredOutboxStore.PublishedMessageIds.Count, "Backfill outbox publisher should mark the published backfill outbox message as published.", failures);
Expect.True(firstBackfillBrokerHandleResult.HasProcessedRequest, "Backfill broker handler should process a broker-delivered backfill request when it has not been handled before.", failures);
Expect.Equal("completed", firstBackfillBrokerHandleResult.Status ?? string.Empty, "Backfill broker handler should complete the queued backfill request through the execution service.", failures);
Expect.False(secondBackfillBrokerHandleResult.HasProcessedRequest, "Backfill broker handler should stay idempotent for already processed backfill broker messages.", failures);
Expect.Equal(1, backfillBrokeredInboxStore.ProcessedMessages.Count, "Backfill broker handler should record inbox idempotency for processed backfill broker messages.", failures);
Expect.Equal("completed", brokeredCompletedBackfill.Status, "Backfill broker handler should leave the backfill request in the completed state after broker delivery.", failures);

var alertRepository = new InMemoryBillAlertRepository();
var alertProjection = new InMemoryBillAlertProjectionStore();
var alertGenerationService = new AlertGenerationService(alertRepository, alertProjection);
var alertsQueryService = new AlertsQueryService(alertProjection);

await alertGenerationService.GenerateAlertsAsync(listedBills, profiles, new DateTimeOffset(2026, 03, 25, 12, 00, 00, TimeSpan.Zero), CancellationToken.None);
await alertGenerationService.GenerateAlertsAsync(listedBills, profiles, new DateTimeOffset(2026, 03, 25, 12, 30, 00, TimeSpan.Zero), CancellationToken.None);

var alerts = await alertsQueryService.GetAlertsAsync(CancellationToken.None);

Expect.Equal(2, alerts.Count, "Alert generation should create one alert per matching bill/profile pair and deduplicate reruns.", failures);
Expect.Equal("VAT", alerts[0].MatchedKeywords.First(), "Alerts should retain the matched keyword that triggered the notification.", failures);
Expect.Equal("VAT", alerts[0].ProfileName, "Alerts should expose the matching monitoring profile name.", failures);
Expect.Equal("Ustawa o zmianie VAT", alerts[0].BillTitle, "Alerts should expose the matched bill title.", failures);
Expect.Equal("immediate", alerts[1].AlertPolicy, "Alerts should preserve the profile alert policy in the read model.", failures);
Expect.SequenceEqual(["CIT"], alerts[1].MatchedKeywords, "Alerts should project deduplicated keyword matches into the API contract.", failures);

await webhookCommandService.RegisterAsync(new RegisterWebhookCommand(
    Guid.Parse("78EB559F-3C47-4748-A22A-C5D42B6194E7"),
    "Alert feed",
    "https://hooks.example.test/lawwatcher/alerts",
    ["alert.created"]), CancellationToken.None);

var webhookEventDispatcher = new InMemoryWebhookDispatcher();
var webhookAlertFeed = new BillAlertWebhookReadRepositoryAdapter(alertProjection);
var webhookDispatchStore = new InMemoryWebhookEventDispatchStore();
var webhookDispatchService = new AlertWebhookDispatchService(
    webhookAlertFeed,
    webhookProjection,
    webhookEventDispatcher,
    webhookDispatchStore);
var webhookDispatchQueryService = new WebhookEventDispatchesQueryService(webhookDispatchStore);

var firstWebhookDispatchRun = await webhookDispatchService.DispatchPendingAsync(CancellationToken.None);
var secondWebhookDispatchRun = await webhookDispatchService.DispatchPendingAsync(CancellationToken.None);
var integrationWebhookDispatches = await webhookDispatchQueryService.GetDispatchesAsync(CancellationToken.None);
var rawWebhookDispatches = await webhookEventDispatcher.GetDispatchesAsync(CancellationToken.None);

Expect.Equal(2, firstWebhookDispatchRun.ProcessedCount, "Webhook dispatch service should deliver alert.created for each matching alert to active webhook registrations.", failures);
Expect.Equal(0, secondWebhookDispatchRun.ProcessedCount, "Webhook dispatch service should deduplicate already delivered alert/registration pairs.", failures);
Expect.Equal(2, integrationWebhookDispatches.Count, "Webhook dispatch query service should expose persisted integration webhook deliveries.", failures);
Expect.True(integrationWebhookDispatches.All(dispatch => dispatch.EventType == "alert.created"), "Webhook dispatch query service should preserve the integration event type for all alert webhook deliveries.", failures);
Expect.True(integrationWebhookDispatches.All(dispatch => dispatch.CallbackUrl == "https://hooks.example.test/lawwatcher/alerts"), "Webhook dispatch query service should preserve the target callback URL for all dispatched alerts.", failures);
Expect.Equal(2, rawWebhookDispatches.Count, "Webhook dispatcher adapter should record one outbound integration webhook dispatch per matching alert.", failures);
Expect.True(rawWebhookDispatches.All(dispatch => dispatch.EventType == "alert.created"), "Webhook dispatcher adapter should send the alert.created event type to integration webhooks.", failures);

var brokeredWebhookRepository = new InMemoryWebhookRegistrationRepository();
var brokeredWebhookProjection = new InMemoryWebhookRegistrationProjectionStore();
var brokeredWebhookCommandService = new WebhookRegistrationsCommandService(brokeredWebhookRepository, brokeredWebhookProjection);
var brokeredWebhookOutboxStore = new InMemoryOutboxMessageStore();
var brokeredWebhookPublisher = new RecordingIntegrationEventPublisher();
var brokeredWebhookInboxStore = new InMemoryInboxStore();
var webhookRegistrationOutboxPublisher = new WebhookRegistrationOutboxPublisher(brokeredWebhookOutboxStore, brokeredWebhookPublisher);
var brokeredWebhookDispatcherAdapter = new InMemoryWebhookDispatcher();
var brokeredWebhookDispatchStore = new InMemoryWebhookEventDispatchStore();
var brokeredWebhookMessageHandler = new WebhookRegistrationDispatchMessageHandler(
    new AlertWebhookDispatchService(
        new BillAlertWebhookReadRepositoryAdapter(alertProjection),
        brokeredWebhookProjection,
        brokeredWebhookDispatcherAdapter,
        brokeredWebhookDispatchStore),
    brokeredWebhookInboxStore);

await brokeredWebhookCommandService.RegisterAsync(new RegisterWebhookCommand(
    Guid.Parse("5A85CF08-95B0-4861-B2B5-DFDD91E64A93"),
    "Broker Alert Feed",
    "https://hooks.example.test/broker-alerts",
    ["alert.created"]), CancellationToken.None);
await brokeredWebhookOutboxStore.EnqueueAsync(new WebhookRegisteredIntegrationEvent(
    Guid.Parse("8BFB1C5D-2AF2-4C7F-A74E-E440E93D9CB7"),
    DateTimeOffset.UtcNow,
    Guid.Parse("5A85CF08-95B0-4861-B2B5-DFDD91E64A93"),
    "Broker Alert Feed",
    "https://hooks.example.test/broker-alerts",
    ["alert.created"]), CancellationToken.None);

var webhookRegistrationOutboxPublishResult = await webhookRegistrationOutboxPublisher.PublishPendingAsync(maxMessages: 1, CancellationToken.None);
var publishedWebhookRegistrationEvent = brokeredWebhookPublisher.PublishedEvents.OfType<WebhookRegisteredIntegrationEvent>().Single();
var firstWebhookBrokerHandleResult = await brokeredWebhookMessageHandler.HandleAsync(publishedWebhookRegistrationEvent, CancellationToken.None);
var secondWebhookBrokerHandleResult = await brokeredWebhookMessageHandler.HandleAsync(publishedWebhookRegistrationEvent, CancellationToken.None);
var brokeredWebhookDispatches = await new WebhookEventDispatchesQueryService(brokeredWebhookDispatchStore).GetDispatchesAsync(CancellationToken.None);
var brokeredWebhookRawDispatches = await brokeredWebhookDispatcherAdapter.GetDispatchesAsync(CancellationToken.None);

Expect.Equal(1, webhookRegistrationOutboxPublishResult.PublishedCount, "Webhook registration outbox publisher should publish one pending webhook integration event to the broker port.", failures);
Expect.False(webhookRegistrationOutboxPublishResult.HasRemainingMessages, "Webhook registration outbox publisher should report no remaining webhook integration events after publishing the pending batch.", failures);
Expect.Equal(1, brokeredWebhookPublisher.PublishedEvents.Count, "Webhook registration outbox publisher should pass the webhook integration event to the broker port exactly once.", failures);
Expect.Equal(1, brokeredWebhookOutboxStore.PublishedMessageIds.Count, "Webhook registration outbox publisher should mark the published webhook outbox message as published.", failures);
Expect.Equal(2, firstWebhookBrokerHandleResult.ProcessedCount, "Webhook registration broker handler should catch up all matching alert webhooks for the new registration.", failures);
Expect.Equal(0, secondWebhookBrokerHandleResult.ProcessedCount, "Webhook registration broker handler should stay idempotent for already processed broker messages.", failures);
Expect.Equal(1, brokeredWebhookInboxStore.ProcessedMessages.Count, "Webhook registration broker handler should record inbox idempotency for processed broker messages.", failures);
Expect.Equal(2, brokeredWebhookDispatches.Count, "Webhook registration broker handler should persist caught-up webhook dispatch records.", failures);
Expect.Equal(2, brokeredWebhookRawDispatches.Count, "Webhook registration broker handler should emit the webhook deliveries through the dispatcher adapter.", failures);

var dispatchSubscriptionRepository = new InMemoryProfileSubscriptionRepository();
var dispatchSubscriptionProjection = new InMemoryProfileSubscriptionProjectionStore();
var dispatchSubscriptionCommandService = new ProfileSubscriptionsCommandService(dispatchSubscriptionRepository, dispatchSubscriptionProjection);
await dispatchSubscriptionCommandService.CreateAsync(new CreateProfileSubscriptionCommand(
    Guid.Parse("9D5FC250-4A08-44C0-848F-F13A4A461816"),
    alerts[1].ProfileId,
    alerts[1].ProfileName,
    "anna.nowak@example.test",
    SubscriptionChannel.Email(),
    AlertPolicy.Immediate()), CancellationToken.None);
await dispatchSubscriptionCommandService.CreateAsync(new CreateProfileSubscriptionCommand(
    Guid.Parse("F3F8E913-632B-4CE4-AEEA-9A021D22FB7C"),
    alerts[1].ProfileId,
    alerts[1].ProfileName,
    "https://audit.example.test/lawwatcher/alerts",
    SubscriptionChannel.Webhook(),
    AlertPolicy.Immediate()), CancellationToken.None);
await dispatchSubscriptionCommandService.CreateAsync(new CreateProfileSubscriptionCommand(
    Guid.Parse("0DAA9FB8-E955-4722-B0DE-4E7FA780B522"),
    alerts[0].ProfileId,
    alerts[0].ProfileName,
    "marek.kowalski@example.test",
    SubscriptionChannel.Email(),
    AlertPolicy.Digest(TimeSpan.FromHours(12))), CancellationToken.None);

var alertWebhookDispatcher = new InMemoryWebhookDispatcher();
var emailChannel = new InMemoryEmailNotificationChannel();
var webhookChannel = new WebhookNotificationChannel(alertWebhookDispatcher);
var notificationDispatchStore = new InMemoryAlertNotificationDispatchStore();
var notificationSubscriptions = new ProfileSubscriptionNotificationReadRepositoryAdapter(dispatchSubscriptionProjection);
var notificationDispatchService = new AlertNotificationDispatchService(
    alertProjection,
    notificationSubscriptions,
    [emailChannel, webhookChannel],
    notificationDispatchStore);
var notificationDispatchQueryService = new AlertNotificationDispatchesQueryService(notificationDispatchStore);

var firstDispatchRun = await notificationDispatchService.DispatchPendingAsync(CancellationToken.None);
var secondDispatchRun = await notificationDispatchService.DispatchPendingAsync(CancellationToken.None);
var notificationDispatches = await notificationDispatchQueryService.GetDispatchesAsync(CancellationToken.None);
var emailDispatches = await emailChannel.GetDispatchesAsync(CancellationToken.None);
var webhookAlertDispatches = await alertWebhookDispatcher.GetDispatchesAsync(CancellationToken.None);

Expect.Equal(2, firstDispatchRun.ProcessedCount, "Alert notification dispatch service should deliver immediate alerts to all matching channels.", failures);
Expect.Equal(1, firstDispatchRun.SkippedDigestCount, "Alert notification dispatch service should skip digest subscriptions during immediate dispatch.", failures);
Expect.Equal(0, secondDispatchRun.ProcessedCount, "Alert notification dispatch service should deduplicate already delivered alert/subscription pairs.", failures);
Expect.Equal(2, notificationDispatches.Count, "Notification dispatch query service should expose persisted dispatch records.", failures);
Expect.Equal(1, emailDispatches.Count, "Email notification channel should receive exactly one immediate email alert.", failures);
Expect.Equal("anna.nowak@example.test", emailDispatches[0].Recipient, "Email notification channel should preserve the subscriber address.", failures);
Expect.Equal(1, webhookAlertDispatches.Count, "Webhook notification channel should dispatch exactly one immediate webhook alert.", failures);
Expect.Equal("https://audit.example.test/lawwatcher/alerts", webhookAlertDispatches[0].CallbackUrl, "Webhook notification channel should use the webhook subscriber address as callback URL.", failures);
Expect.Equal("notification.alert.created", webhookAlertDispatches[0].EventType, "Webhook notification channel should preserve the alert event type.", failures);

var brokeredSubscriptionRepository = new InMemoryProfileSubscriptionRepository();
var brokeredSubscriptionProjection = new InMemoryProfileSubscriptionProjectionStore();
var brokeredSubscriptionCommandService = new ProfileSubscriptionsCommandService(brokeredSubscriptionRepository, brokeredSubscriptionProjection);
var brokeredSubscriptionOutboxStore = new InMemoryOutboxMessageStore();
var brokeredSubscriptionPublisher = new RecordingIntegrationEventPublisher();
var brokeredSubscriptionInboxStore = new InMemoryInboxStore();
var profileSubscriptionOutboxPublisher = new ProfileSubscriptionOutboxPublisher(brokeredSubscriptionOutboxStore, brokeredSubscriptionPublisher);
var brokeredNotificationChannel = new InMemoryEmailNotificationChannel();
var brokeredNotificationDispatchStore = new InMemoryAlertNotificationDispatchStore();
var brokeredProfileSubscriptionMessageHandler = new ProfileSubscriptionNotificationMessageHandler(
    new AlertNotificationDispatchService(
        alertProjection,
        new ProfileSubscriptionNotificationReadRepositoryAdapter(brokeredSubscriptionProjection),
        [brokeredNotificationChannel],
        brokeredNotificationDispatchStore),
    brokeredSubscriptionInboxStore);

await brokeredSubscriptionCommandService.CreateAsync(new CreateProfileSubscriptionCommand(
    Guid.Parse("7BB435F7-0D48-4C90-8739-B6920346CCBC"),
    alerts[1].ProfileId,
    alerts[1].ProfileName,
    "broker-subscription@example.test",
    SubscriptionChannel.Email(),
    AlertPolicy.Immediate()), CancellationToken.None);
await brokeredSubscriptionOutboxStore.EnqueueAsync(new ProfileSubscriptionCreatedIntegrationEvent(
    Guid.Parse("96A66773-2B73-4CC2-A64A-7D76450A9DD8"),
    DateTimeOffset.UtcNow,
    Guid.Parse("7BB435F7-0D48-4C90-8739-B6920346CCBC"),
    alerts[1].ProfileId,
    alerts[1].ProfileName,
    "broker-subscription@example.test",
    "email",
    "immediate",
    null), CancellationToken.None);

var profileSubscriptionOutboxPublishResult = await profileSubscriptionOutboxPublisher.PublishPendingAsync(maxMessages: 1, CancellationToken.None);
var publishedProfileSubscriptionEvent = brokeredSubscriptionPublisher.PublishedEvents.OfType<ProfileSubscriptionCreatedIntegrationEvent>().Single();
var firstProfileSubscriptionBrokerHandleResult = await brokeredProfileSubscriptionMessageHandler.HandleAsync(publishedProfileSubscriptionEvent, CancellationToken.None);
var secondProfileSubscriptionBrokerHandleResult = await brokeredProfileSubscriptionMessageHandler.HandleAsync(publishedProfileSubscriptionEvent, CancellationToken.None);
var brokeredNotificationDispatches = await new AlertNotificationDispatchesQueryService(brokeredNotificationDispatchStore).GetDispatchesAsync(CancellationToken.None);
var brokeredChannelDispatches = await brokeredNotificationChannel.GetDispatchesAsync(CancellationToken.None);

Expect.Equal(1, profileSubscriptionOutboxPublishResult.PublishedCount, "Profile subscription outbox publisher should publish one pending subscription integration event to the broker port.", failures);
Expect.False(profileSubscriptionOutboxPublishResult.HasRemainingMessages, "Profile subscription outbox publisher should report no remaining subscription integration events after publishing the pending batch.", failures);
Expect.Equal(1, brokeredSubscriptionPublisher.PublishedEvents.Count, "Profile subscription outbox publisher should pass the subscription integration event to the broker port exactly once.", failures);
Expect.Equal(1, brokeredSubscriptionOutboxStore.PublishedMessageIds.Count, "Profile subscription outbox publisher should mark the published subscription outbox message as published.", failures);
Expect.Equal(1, firstProfileSubscriptionBrokerHandleResult.ProcessedCount, "Profile subscription broker handler should catch up one immediate notification for the new subscription.", failures);
Expect.Equal(0, firstProfileSubscriptionBrokerHandleResult.SkippedDigestCount, "Profile subscription broker handler should not skip digest notifications for an immediate subscription.", failures);
Expect.Equal(0, secondProfileSubscriptionBrokerHandleResult.ProcessedCount, "Profile subscription broker handler should stay idempotent for already processed broker messages.", failures);
Expect.Equal(1, brokeredSubscriptionInboxStore.ProcessedMessages.Count, "Profile subscription broker handler should record inbox idempotency for processed broker messages.", failures);
Expect.Equal(1, brokeredNotificationDispatches.Count, "Profile subscription broker handler should persist the caught-up notification dispatch.", failures);
Expect.Equal(1, brokeredChannelDispatches.Count, "Profile subscription broker handler should emit the immediate notification through the configured channel.", failures);

var brokeredAlertOutboxStore = new InMemoryOutboxMessageStore();
var brokeredAlertPublisher = new RecordingIntegrationEventPublisher();
var brokeredAlertNotificationInboxStore = new InMemoryInboxStore();
var brokeredAlertWebhookInboxStore = new InMemoryInboxStore();
var billAlertCreatedOutboxPublisher = new BillAlertCreatedOutboxPublisher(brokeredAlertOutboxStore, brokeredAlertPublisher);
var brokeredAlertNotificationChannel = new InMemoryEmailNotificationChannel();
var brokeredAlertNotificationDispatchStore = new InMemoryAlertNotificationDispatchStore();
var brokeredAlertWebhookDispatcher = new InMemoryWebhookDispatcher();
var brokeredAlertWebhookDispatchStore = new InMemoryWebhookEventDispatchStore();
var brokeredAlertSubscriptionProjection = new InMemoryProfileSubscriptionProjectionStore();
var brokeredAlertSubscriptionCommandService = new ProfileSubscriptionsCommandService(
    new InMemoryProfileSubscriptionRepository(),
    brokeredAlertSubscriptionProjection);
var brokeredAlertWebhookProjection = new InMemoryWebhookRegistrationProjectionStore();
var brokeredAlertWebhookCommandService = new WebhookRegistrationsCommandService(
    new InMemoryWebhookRegistrationRepository(),
    brokeredAlertWebhookProjection);
var brokeredBillAlertNotificationMessageHandler = new BillAlertNotificationMessageHandler(
    new AlertNotificationDispatchService(
        alertProjection,
        new ProfileSubscriptionNotificationReadRepositoryAdapter(brokeredAlertSubscriptionProjection),
        [brokeredAlertNotificationChannel],
        brokeredAlertNotificationDispatchStore),
    brokeredAlertNotificationInboxStore);
var brokeredBillAlertWebhookMessageHandler = new BillAlertWebhookMessageHandler(
    new AlertWebhookDispatchService(
        new BillAlertWebhookReadRepositoryAdapter(alertProjection),
        brokeredAlertWebhookProjection,
        brokeredAlertWebhookDispatcher,
        brokeredAlertWebhookDispatchStore),
    brokeredAlertWebhookInboxStore);

await brokeredAlertSubscriptionCommandService.CreateAsync(new CreateProfileSubscriptionCommand(
    Guid.Parse("63AC1F3C-3B95-4F3A-A63C-9123F718E2D3"),
    alerts[1].ProfileId,
    alerts[1].ProfileName,
    "broker-alert-created@example.test",
    SubscriptionChannel.Email(),
    AlertPolicy.Immediate()), CancellationToken.None);
await brokeredAlertWebhookCommandService.RegisterAsync(new RegisterWebhookCommand(
    Guid.Parse("CCF3C92B-4064-4020-9484-B3A7B6FF2806"),
    "Broker Bill Alert Feed",
    "https://hooks.example.test/broker-bill-alert",
    ["alert.created"]), CancellationToken.None);
await brokeredAlertOutboxStore.EnqueueAsync(new BillAlertCreatedIntegrationEvent(
    Guid.Parse("8FA8E66F-8F54-49BF-8B44-006959C577E2"),
    DateTimeOffset.UtcNow,
    alerts[1].Id,
    alerts[1].ProfileId,
    alerts[1].ProfileName,
    alerts[1].BillId,
    alerts[1].BillTitle,
    alerts[1].BillExternalId,
    alerts[1].BillSubmittedOn,
    alerts[1].AlertPolicy,
    alerts[1].MatchedKeywords), CancellationToken.None);

var billAlertCreatedOutboxPublishResult = await billAlertCreatedOutboxPublisher.PublishPendingAsync(maxMessages: 1, CancellationToken.None);
var publishedBillAlertCreatedEvent = brokeredAlertPublisher.PublishedEvents.OfType<BillAlertCreatedIntegrationEvent>().Single();
var firstBillAlertNotificationBrokerHandleResult = await brokeredBillAlertNotificationMessageHandler.HandleAsync(publishedBillAlertCreatedEvent, CancellationToken.None);
var secondBillAlertNotificationBrokerHandleResult = await brokeredBillAlertNotificationMessageHandler.HandleAsync(publishedBillAlertCreatedEvent, CancellationToken.None);
var firstBillAlertWebhookBrokerHandleResult = await brokeredBillAlertWebhookMessageHandler.HandleAsync(publishedBillAlertCreatedEvent, CancellationToken.None);
var secondBillAlertWebhookBrokerHandleResult = await brokeredBillAlertWebhookMessageHandler.HandleAsync(publishedBillAlertCreatedEvent, CancellationToken.None);
var brokeredBillAlertNotificationDispatches = await new AlertNotificationDispatchesQueryService(brokeredAlertNotificationDispatchStore).GetDispatchesAsync(CancellationToken.None);
var brokeredBillAlertChannelDispatches = await brokeredAlertNotificationChannel.GetDispatchesAsync(CancellationToken.None);
var brokeredBillAlertWebhookDispatches = await new WebhookEventDispatchesQueryService(brokeredAlertWebhookDispatchStore).GetDispatchesAsync(CancellationToken.None);
var brokeredBillAlertRawWebhookDispatches = await brokeredAlertWebhookDispatcher.GetDispatchesAsync(CancellationToken.None);

Expect.Equal(1, billAlertCreatedOutboxPublishResult.PublishedCount, "Bill alert outbox publisher should publish one pending bill alert integration event to the broker port.", failures);
Expect.False(billAlertCreatedOutboxPublishResult.HasRemainingMessages, "Bill alert outbox publisher should report no remaining bill alert integration events after publishing the pending batch.", failures);
Expect.Equal(1, brokeredAlertPublisher.PublishedEvents.Count, "Bill alert outbox publisher should pass the bill alert integration event to the broker port exactly once.", failures);
Expect.Equal(1, brokeredAlertOutboxStore.PublishedMessageIds.Count, "Bill alert outbox publisher should mark the published bill alert outbox message as published.", failures);
Expect.Equal(1, firstBillAlertNotificationBrokerHandleResult.ProcessedCount, "Bill alert notification broker handler should dispatch one immediate notification for the matching subscription.", failures);
Expect.Equal(0, firstBillAlertNotificationBrokerHandleResult.SkippedDigestCount, "Bill alert notification broker handler should not skip the matching immediate subscription.", failures);
Expect.Equal(0, secondBillAlertNotificationBrokerHandleResult.ProcessedCount, "Bill alert notification broker handler should stay idempotent for already processed broker messages.", failures);
Expect.Equal(1, brokeredAlertNotificationInboxStore.ProcessedMessages.Count, "Bill alert notification broker handler should record inbox idempotency for processed broker messages.", failures);
Expect.Equal(1, brokeredBillAlertNotificationDispatches.Count, "Bill alert notification broker handler should persist the caught-up notification dispatch.", failures);
Expect.Equal(1, brokeredBillAlertChannelDispatches.Count, "Bill alert notification broker handler should emit the immediate notification through the configured channel.", failures);
Expect.Equal(1, firstBillAlertWebhookBrokerHandleResult.ProcessedCount, "Bill alert webhook broker handler should dispatch one matching alert.created webhook for the new bill alert.", failures);
Expect.Equal(0, secondBillAlertWebhookBrokerHandleResult.ProcessedCount, "Bill alert webhook broker handler should stay idempotent for already processed broker messages.", failures);
Expect.Equal(1, brokeredAlertWebhookInboxStore.ProcessedMessages.Count, "Bill alert webhook broker handler should record inbox idempotency for processed broker messages.", failures);
Expect.Equal(1, brokeredBillAlertWebhookDispatches.Count, "Bill alert webhook broker handler should persist the caught-up webhook dispatch.", failures);
Expect.Equal(1, brokeredBillAlertRawWebhookDispatches.Count, "Bill alert webhook broker handler should emit the matching webhook delivery through the dispatcher adapter.", failures);

var brokeredBillProjectionOutboxStore = new InMemoryOutboxMessageStore();
var brokeredBillProjectionPublisher = new RecordingIntegrationEventPublisher();
var brokeredBillProjectionInboxStore = new InMemoryInboxStore();
var brokeredBillProjectionOrchestrator = new CountingBillProjectionRefreshOrchestrator();
var billProjectionOutboxPublisher = new BillProjectionOutboxPublisher(
    brokeredBillProjectionOutboxStore,
    brokeredBillProjectionPublisher);
var billProjectionMessageHandler = new BillProjectionMessageHandler(
    brokeredBillProjectionOrchestrator,
    brokeredBillProjectionInboxStore);

await brokeredBillProjectionOutboxStore.EnqueueAsync(new BillImportedIntegrationEvent(
    Guid.Parse("A37BB9EE-A18D-4843-BE48-E55D4C5F0FE4"),
    DateTimeOffset.UtcNow,
    Guid.Parse("8E9839F4-520F-4D57-AC9E-9B7868BE5125"),
    "Sejm",
    "druk-999",
    "https://sejm.example.test/bills/broker-bill",
    "Ustawa o zmianie akcyzy",
    new DateOnly(2026, 03, 21)), CancellationToken.None);

var billProjectionOutboxPublishResult = await billProjectionOutboxPublisher.PublishPendingAsync(maxMessages: 1, CancellationToken.None);
var publishedBillImportedEvent = brokeredBillProjectionPublisher.PublishedEvents.OfType<BillImportedIntegrationEvent>().Single();
var firstBillProjectionBrokerHandleResult = await billProjectionMessageHandler.HandleAsync(publishedBillImportedEvent, CancellationToken.None);
var secondBillProjectionBrokerHandleResult = await billProjectionMessageHandler.HandleAsync(publishedBillImportedEvent, CancellationToken.None);

Expect.Equal(1, billProjectionOutboxPublishResult.PublishedCount, "Bill projection outbox publisher should publish one pending bill integration event to the broker port.", failures);
Expect.False(billProjectionOutboxPublishResult.HasRemainingMessages, "Bill projection outbox publisher should report no remaining bill integration events after publishing the pending batch.", failures);
Expect.Equal(1, brokeredBillProjectionPublisher.PublishedEvents.Count, "Bill projection outbox publisher should pass the bill integration event to the broker port exactly once.", failures);
Expect.Equal(1, brokeredBillProjectionOutboxStore.PublishedMessageIds.Count, "Bill projection outbox publisher should mark the published bill integration event as published.", failures);
Expect.True(firstBillProjectionBrokerHandleResult.HasRefreshed, "Bill projection broker handler should refresh projections when a bill integration event is delivered for the first time.", failures);
Expect.False(secondBillProjectionBrokerHandleResult.HasRefreshed, "Bill projection broker handler should stay idempotent for already processed bill broker messages.", failures);
Expect.Equal(1, brokeredBillProjectionInboxStore.ProcessedMessages.Count, "Bill projection broker handler should record inbox idempotency for processed bill broker messages.", failures);
Expect.Equal(1, brokeredBillProjectionOrchestrator.InvocationCount, "Bill projection broker handler should invoke the projection refresh orchestrator exactly once for a unique bill broker message.", failures);

var brokeredProcessProjectionOutboxStore = new InMemoryOutboxMessageStore();
var brokeredProcessProjectionPublisher = new RecordingIntegrationEventPublisher();
var brokeredProcessProjectionInboxStore = new InMemoryInboxStore();
var brokeredProcessProjectionOrchestrator = new CountingProcessProjectionRefreshOrchestrator();
var processProjectionOutboxPublisher = new ProcessProjectionOutboxPublisher(
    brokeredProcessProjectionOutboxStore,
    brokeredProcessProjectionPublisher);
var processProjectionMessageHandler = new ProcessProjectionMessageHandler(
    brokeredProcessProjectionOrchestrator,
    brokeredProcessProjectionInboxStore);

await brokeredProcessProjectionOutboxStore.EnqueueAsync(new LegislativeProcessStartedIntegrationEvent(
    Guid.Parse("C64D1E66-25D5-4F3F-B60A-1FD88905A043"),
    DateTimeOffset.UtcNow,
    Guid.Parse("E2037A16-85DE-480B-8A62-2CF51E1C8FD2"),
    Guid.Parse("8E9839F4-520F-4D57-AC9E-9B7868BE5125"),
    "Ustawa o zmianie akcyzy",
    "druk-999",
    "first-reading",
    "Pierwsze czytanie",
    new DateOnly(2026, 03, 22)), CancellationToken.None);

var processProjectionOutboxPublishResult = await processProjectionOutboxPublisher.PublishPendingAsync(maxMessages: 1, CancellationToken.None);
var publishedProcessStartedEvent = brokeredProcessProjectionPublisher.PublishedEvents.OfType<LegislativeProcessStartedIntegrationEvent>().Single();
var firstProcessProjectionBrokerHandleResult = await processProjectionMessageHandler.HandleAsync(publishedProcessStartedEvent, CancellationToken.None);
var secondProcessProjectionBrokerHandleResult = await processProjectionMessageHandler.HandleAsync(publishedProcessStartedEvent, CancellationToken.None);

Expect.Equal(1, processProjectionOutboxPublishResult.PublishedCount, "Process projection outbox publisher should publish one pending legislative process integration event to the broker port.", failures);
Expect.False(processProjectionOutboxPublishResult.HasRemainingMessages, "Process projection outbox publisher should report no remaining legislative process integration events after publishing the pending batch.", failures);
Expect.Equal(1, brokeredProcessProjectionPublisher.PublishedEvents.Count, "Process projection outbox publisher should pass the legislative process integration event to the broker port exactly once.", failures);
Expect.Equal(1, brokeredProcessProjectionOutboxStore.PublishedMessageIds.Count, "Process projection outbox publisher should mark the published legislative process integration event as published.", failures);
Expect.True(firstProcessProjectionBrokerHandleResult.HasRefreshed, "Process projection broker handler should refresh projections when a legislative process integration event is delivered for the first time.", failures);
Expect.False(secondProcessProjectionBrokerHandleResult.HasRefreshed, "Process projection broker handler should stay idempotent for already processed legislative process broker messages.", failures);
Expect.Equal(1, brokeredProcessProjectionInboxStore.ProcessedMessages.Count, "Process projection broker handler should record inbox idempotency for processed legislative process broker messages.", failures);
Expect.Equal(1, brokeredProcessProjectionOrchestrator.InvocationCount, "Process projection broker handler should invoke the projection refresh orchestrator exactly once for a unique legislative process broker message.", failures);

var brokeredActProjectionOutboxStore = new InMemoryOutboxMessageStore();
var brokeredActProjectionPublisher = new RecordingIntegrationEventPublisher();
var brokeredActProjectionInboxStore = new InMemoryInboxStore();
var brokeredActProjectionOrchestrator = new CountingActProjectionRefreshOrchestrator();
var actProjectionOutboxPublisher = new ActProjectionOutboxPublisher(
    brokeredActProjectionOutboxStore,
    brokeredActProjectionPublisher);
var actProjectionMessageHandler = new ActProjectionMessageHandler(
    brokeredActProjectionOrchestrator,
    brokeredActProjectionInboxStore);

await brokeredActProjectionOutboxStore.EnqueueAsync(new PublishedActRegisteredIntegrationEvent(
    Guid.Parse("65C9E6B5-561C-4466-A46D-D0C309912B78"),
    DateTimeOffset.UtcNow,
    Guid.Parse("7A4B9C90-7848-4C10-9452-E4F54B4D8F91"),
    Guid.Parse("8E9839F4-520F-4D57-AC9E-9B7868BE5125"),
    "Ustawa o zmianie akcyzy",
    "druk-999",
    "https://eli.gov.pl/eli/DU/2026/600/ogl",
    "Ustawa z dnia 31 marca 2026 r. o zmianie ustawy o podatku akcyzowym",
    new DateOnly(2026, 03, 31),
    new DateOnly(2026, 04, 15)), CancellationToken.None);

var actProjectionOutboxPublishResult = await actProjectionOutboxPublisher.PublishPendingAsync(maxMessages: 1, CancellationToken.None);
var publishedActRegisteredEvent = brokeredActProjectionPublisher.PublishedEvents.OfType<PublishedActRegisteredIntegrationEvent>().Single();
var firstActProjectionBrokerHandleResult = await actProjectionMessageHandler.HandleAsync(publishedActRegisteredEvent, CancellationToken.None);
var secondActProjectionBrokerHandleResult = await actProjectionMessageHandler.HandleAsync(publishedActRegisteredEvent, CancellationToken.None);

Expect.Equal(1, actProjectionOutboxPublishResult.PublishedCount, "Act projection outbox publisher should publish one pending act integration event to the broker port.", failures);
Expect.False(actProjectionOutboxPublishResult.HasRemainingMessages, "Act projection outbox publisher should report no remaining act integration events after publishing the pending batch.", failures);
Expect.Equal(1, brokeredActProjectionPublisher.PublishedEvents.Count, "Act projection outbox publisher should pass the act integration event to the broker port exactly once.", failures);
Expect.Equal(1, brokeredActProjectionOutboxStore.PublishedMessageIds.Count, "Act projection outbox publisher should mark the published act integration event as published.", failures);
Expect.True(firstActProjectionBrokerHandleResult.HasRefreshed, "Act projection broker handler should refresh projections when an act integration event is delivered for the first time.", failures);
Expect.False(secondActProjectionBrokerHandleResult.HasRefreshed, "Act projection broker handler should stay idempotent for already processed act broker messages.", failures);
Expect.Equal(1, brokeredActProjectionInboxStore.ProcessedMessages.Count, "Act projection broker handler should record inbox idempotency for processed act broker messages.", failures);
Expect.Equal(1, brokeredActProjectionOrchestrator.InvocationCount, "Act projection broker handler should invoke the projection refresh orchestrator exactly once for a unique act broker message.", failures);

var brokeredMonitoringProfileProjectionOutboxStore = new InMemoryOutboxMessageStore();
var brokeredMonitoringProfileProjectionPublisher = new RecordingIntegrationEventPublisher();
var brokeredMonitoringProfileProjectionInboxStore = new InMemoryInboxStore();
var brokeredMonitoringProfileProjectionOrchestrator = new CountingMonitoringProfileProjectionRefreshOrchestrator();
var monitoringProfileProjectionOutboxPublisher = new MonitoringProfileProjectionOutboxPublisher(
    brokeredMonitoringProfileProjectionOutboxStore,
    brokeredMonitoringProfileProjectionPublisher);
var monitoringProfileProjectionMessageHandler = new MonitoringProfileProjectionMessageHandler(
    brokeredMonitoringProfileProjectionOrchestrator,
    brokeredMonitoringProfileProjectionInboxStore);

await brokeredMonitoringProfileProjectionOutboxStore.EnqueueAsync(new MonitoringProfileCreatedIntegrationEvent(
    Guid.Parse("B9B4F80F-C10B-4B35-8D1B-218487B5A0A4"),
    DateTimeOffset.UtcNow,
    Guid.Parse("24B5A98F-6179-4BE8-84FA-B606F10A8F31"),
    "Broker Monitoring Profile",
    "immediate",
    null), CancellationToken.None);
await brokeredMonitoringProfileProjectionOutboxStore.EnqueueAsync(new MonitoringProfileAlertPolicyChangedIntegrationEvent(
    Guid.Parse("6E1E70F8-6DB7-4A70-94D1-2AB6E31D132D"),
    DateTimeOffset.UtcNow.AddMinutes(1),
    Guid.Parse("24B5A98F-6179-4BE8-84FA-B606F10A8F31"),
    "digest",
    TimeSpan.FromHours(6)), CancellationToken.None);

var monitoringProfileProjectionOutboxPublishResult = await monitoringProfileProjectionOutboxPublisher.PublishPendingAsync(maxMessages: 2, CancellationToken.None);
var publishedMonitoringProfileCreatedEvent = brokeredMonitoringProfileProjectionPublisher.PublishedEvents.OfType<MonitoringProfileCreatedIntegrationEvent>().Single();
var publishedMonitoringProfileAlertPolicyChangedEvent = brokeredMonitoringProfileProjectionPublisher.PublishedEvents.OfType<MonitoringProfileAlertPolicyChangedIntegrationEvent>().Single();
var firstMonitoringProfileProjectionBrokerHandleResult = await monitoringProfileProjectionMessageHandler.HandleAsync(publishedMonitoringProfileCreatedEvent, CancellationToken.None);
var alertPolicyChangedMonitoringProfileProjectionBrokerHandleResult = await monitoringProfileProjectionMessageHandler.HandleAsync(publishedMonitoringProfileAlertPolicyChangedEvent, CancellationToken.None);
var secondMonitoringProfileProjectionBrokerHandleResult = await monitoringProfileProjectionMessageHandler.HandleAsync(publishedMonitoringProfileCreatedEvent, CancellationToken.None);

Expect.Equal(2, monitoringProfileProjectionOutboxPublishResult.PublishedCount, "Monitoring profile projection outbox publisher should publish pending monitoring profile integration events to the broker port.", failures);
Expect.False(monitoringProfileProjectionOutboxPublishResult.HasRemainingMessages, "Monitoring profile projection outbox publisher should report no remaining monitoring profile integration events after publishing the pending batch.", failures);
Expect.Equal(2, brokeredMonitoringProfileProjectionPublisher.PublishedEvents.Count, "Monitoring profile projection outbox publisher should pass the monitoring profile integration events to the broker port exactly once each.", failures);
Expect.Equal(2, brokeredMonitoringProfileProjectionOutboxStore.PublishedMessageIds.Count, "Monitoring profile projection outbox publisher should mark the published monitoring profile integration events as published.", failures);
Expect.True(firstMonitoringProfileProjectionBrokerHandleResult.HasRefreshed, "Monitoring profile projection broker handler should refresh projections when a monitoring profile integration event is delivered for the first time.", failures);
Expect.True(alertPolicyChangedMonitoringProfileProjectionBrokerHandleResult.HasRefreshed, "Monitoring profile projection broker handler should refresh projections when a monitoring profile alert-policy change integration event is delivered for the first time.", failures);
Expect.False(secondMonitoringProfileProjectionBrokerHandleResult.HasRefreshed, "Monitoring profile projection broker handler should stay idempotent for already processed monitoring profile broker messages.", failures);
Expect.Equal(2, brokeredMonitoringProfileProjectionInboxStore.ProcessedMessages.Count, "Monitoring profile projection broker handler should record inbox idempotency for processed monitoring profile broker messages.", failures);
Expect.Equal(2, brokeredMonitoringProfileProjectionOrchestrator.InvocationCount, "Monitoring profile projection broker handler should invoke the projection refresh orchestrator exactly once for each unique monitoring profile broker message.", failures);

var durableNotificationRoot = Path.Combine(durableStateRoot, "notifications-and-webhooks");
var durableAlertRoot = Path.Combine(durableNotificationRoot, "alerts");
var durableSubscriptionRoot = Path.Combine(durableNotificationRoot, "subscriptions");
var durableWebhookRegistrationRoot = Path.Combine(durableNotificationRoot, "webhook-registrations");
var durableNotificationDispatchRoot = Path.Combine(durableNotificationRoot, "notification-dispatches");
var durableWebhookDispatchRoot = Path.Combine(durableNotificationRoot, "webhook-dispatches");

var durableAlertRepository = new FileBackedBillAlertRepository(durableAlertRoot);
var durableAlertProjection = new FileBackedBillAlertProjectionStore(durableAlertRoot);
var durableAlertGenerationService = new AlertGenerationService(durableAlertRepository, durableAlertProjection);
await durableAlertGenerationService.GenerateAlertsAsync(listedBills, profiles, new DateTimeOffset(2026, 03, 26, 8, 00, 00, TimeSpan.Zero), CancellationToken.None);

var durableSubscriptionRepository = new FileBackedProfileSubscriptionRepository(durableSubscriptionRoot);
var durableSubscriptionProjection = new FileBackedProfileSubscriptionProjectionStore(durableSubscriptionRoot);
var durableSubscriptionCommandService = new ProfileSubscriptionsCommandService(durableSubscriptionRepository, durableSubscriptionProjection);
await durableSubscriptionCommandService.CreateAsync(new CreateProfileSubscriptionCommand(
    Guid.Parse("245E4746-0C65-4F0B-97A8-0F2D28C8934A"),
    profiles[0].Id,
    profiles[0].Name,
    "anna.nowak@example.test",
    SubscriptionChannel.Email(),
    AlertPolicy.Immediate()), CancellationToken.None);

var durableWebhookRepository = new FileBackedWebhookRegistrationRepository(durableWebhookRegistrationRoot);
var durableWebhookProjection = new FileBackedWebhookRegistrationProjectionStore(durableWebhookRegistrationRoot);
var durableWebhookCommandService = new WebhookRegistrationsCommandService(durableWebhookRepository, durableWebhookProjection);
await durableWebhookCommandService.RegisterAsync(new RegisterWebhookCommand(
    Guid.Parse("51A4623A-9711-4EA5-AC52-31B0D0A8B690"),
    "Durable Alert Feed",
    "https://hooks.example.test/durable-alerts",
    ["alert.created"]), CancellationToken.None);

var reloadedAlertProjection = new FileBackedBillAlertProjectionStore(durableAlertRoot);
var reloadedSubscriptionProjection = new FileBackedProfileSubscriptionProjectionStore(durableSubscriptionRoot);
var reloadedWebhookProjection = new FileBackedWebhookRegistrationProjectionStore(durableWebhookRegistrationRoot);
var durableNotificationDispatchStore = new FileBackedAlertNotificationDispatchStore(durableNotificationDispatchRoot);
var durableWebhookDispatchStore = new FileBackedWebhookEventDispatchStore(durableWebhookDispatchRoot);
var durableWebhookDispatcher = new InMemoryWebhookDispatcher();
var durableEmailChannel = new InMemoryEmailNotificationChannel();
var durableNotificationDispatchService = new AlertNotificationDispatchService(
    reloadedAlertProjection,
    new ProfileSubscriptionNotificationReadRepositoryAdapter(reloadedSubscriptionProjection),
    [durableEmailChannel, new WebhookNotificationChannel(durableWebhookDispatcher)],
    durableNotificationDispatchStore);
var durableWebhookDispatchService = new AlertWebhookDispatchService(
    new BillAlertWebhookReadRepositoryAdapter(reloadedAlertProjection),
    reloadedWebhookProjection,
    durableWebhookDispatcher,
    durableWebhookDispatchStore);

var durableNotificationDispatchFirstRun = await durableNotificationDispatchService.DispatchPendingAsync(CancellationToken.None);
var durableNotificationDispatchSecondRun = await durableNotificationDispatchService.DispatchPendingAsync(CancellationToken.None);
var durableWebhookDispatchFirstRun = await durableWebhookDispatchService.DispatchPendingAsync(CancellationToken.None);
var durableWebhookDispatchSecondRun = await durableWebhookDispatchService.DispatchPendingAsync(CancellationToken.None);

var reloadedNotificationDispatchQuery = new AlertNotificationDispatchesQueryService(
    new FileBackedAlertNotificationDispatchStore(durableNotificationDispatchRoot));
var reloadedWebhookDispatchQuery = new WebhookEventDispatchesQueryService(
    new FileBackedWebhookEventDispatchStore(durableWebhookDispatchRoot));
var durableNotificationDispatches = await reloadedNotificationDispatchQuery.GetDispatchesAsync(CancellationToken.None);
var durableWebhookDispatches = await reloadedWebhookDispatchQuery.GetDispatchesAsync(CancellationToken.None);
var durableRawWebhookDispatches = await durableWebhookDispatcher.GetDispatchesAsync(CancellationToken.None);
var durableEmailDispatches = await durableEmailChannel.GetDispatchesAsync(CancellationToken.None);

Expect.Equal(1, durableNotificationDispatchFirstRun.ProcessedCount, "File-backed notification dispatch store should allow one immediate notification dispatch after store reload.", failures);
Expect.Equal(0, durableNotificationDispatchSecondRun.ProcessedCount, "File-backed notification dispatch store should deduplicate alert/subscription pairs across reruns.", failures);
Expect.Equal(2, durableWebhookDispatchFirstRun.ProcessedCount, "File-backed webhook dispatch store should dispatch all matching alerts to active registrations after store reload.", failures);
Expect.Equal(0, durableWebhookDispatchSecondRun.ProcessedCount, "File-backed webhook dispatch store should deduplicate alert/registration pairs across reruns.", failures);
Expect.Equal(1, durableNotificationDispatches.Count, "File-backed notification dispatch query should expose persisted notification deliveries after reload.", failures);
Expect.Equal(2, durableWebhookDispatches.Count, "File-backed webhook dispatch query should expose persisted integration webhook deliveries after reload.", failures);
Expect.Equal(2, durableRawWebhookDispatches.Count, "Durable dispatch flow should emit the matching integration webhooks through the dispatcher adapter after store reload.", failures);
Expect.Equal(1, durableEmailDispatches.Count, "Durable dispatch flow should still emit the email channel delivery through the adapter.", failures);

var eventFeedProjection = new InMemoryEventFeedProjectionStore();
var eventFeedRefreshService = new EventFeedProjectionRefreshService(
[
    new StubEventFeedSource(
    [
        new EventFeedItem(
            "bill:X-200",
            "bill.imported",
            "bill",
            listedBills[0].Id.ToString("D"),
            listedBills[0].Title,
            "Imported from sejm.",
            new DateTimeOffset(2026, 03, 25, 10, 00, 00, TimeSpan.Zero))
    ]),
    new StubEventFeedSource(
    [
        new EventFeedItem(
            $"alert:{alerts[0].Id:D}",
            "alert.created",
            "alert",
            alerts[0].Id.ToString("D"),
            alerts[0].BillTitle,
            $"Profile: {alerts[0].ProfileName}.",
            new DateTimeOffset(2026, 03, 25, 12, 30, 00, 00, TimeSpan.Zero)),
        new EventFeedItem(
            $"act:{listedActs[0].Id:D}",
            "act.published",
            "act",
            listedActs[0].Id.ToString("D"),
            listedActs[0].Title,
            listedActs[0].Eli,
            new DateTimeOffset(2026, 03, 29, 9, 00, 00, TimeSpan.Zero))
    ])
],
    eventFeedProjection);
var eventFeedRefresh = await eventFeedRefreshService.RefreshAsync(CancellationToken.None);
var eventFeedSecondRefresh = await eventFeedRefreshService.RefreshAsync(CancellationToken.None);
var eventFeedQueryService = new EventFeedQueryService(eventFeedProjection);
var eventFeed = await eventFeedQueryService.GetEventsAsync(CancellationToken.None);
var concurrentEventFeedProjection = new BlockingEventFeedProjection();
var concurrentEventFeedRefreshService = new EventFeedProjectionRefreshService(
[
    new StubEventFeedSource(
    [
        new EventFeedItem(
            "bill:X-200",
            "bill.imported",
            "bill",
            listedBills[0].Id.ToString("D"),
            listedBills[0].Title,
            "Imported from sejm.",
            new DateTimeOffset(2026, 03, 25, 10, 00, 00, TimeSpan.Zero))
    ])
],
    concurrentEventFeedProjection);
var firstConcurrentEventFeedRefreshTask = concurrentEventFeedRefreshService.RefreshAsync(CancellationToken.None);
await concurrentEventFeedProjection.FirstReplaceStarted;
var secondConcurrentEventFeedRefreshTask = concurrentEventFeedRefreshService.RefreshAsync(CancellationToken.None);
await Task.Delay(50);
concurrentEventFeedProjection.AllowFirstReplaceToComplete();
var concurrentEventFeedRefreshResults = await Task.WhenAll(firstConcurrentEventFeedRefreshTask, secondConcurrentEventFeedRefreshTask);

Expect.Equal(3, eventFeedRefresh.EventCount, "Event feed refresh should collect all source events into the durable projection.", failures);
Expect.True(eventFeedRefresh.HasRebuilt, "Event feed refresh should rebuild the projection on the first run.", failures);
Expect.False(eventFeedSecondRefresh.HasRebuilt, "Event feed refresh should skip rewrites when the source fingerprint did not change.", failures);
Expect.Equal(3, eventFeed.Count, "Event feed query service should expose events from the durable projection.", failures);
Expect.Equal("act.published", eventFeed[0].Type, "Event feed query service should sort events by occurrence time descending.", failures);
Expect.Equal("alert.created", eventFeed[1].Type, "Event feed query service should keep later alert events ahead of older bill imports.", failures);
Expect.Equal("bill.imported", eventFeed[2].Type, "Event feed query service should preserve older source events after newer ones.", failures);
Expect.Equal("act", eventFeed[0].SubjectType, "Event feed query service should expose the subject type in the API contract.", failures);
Expect.Equal(1, concurrentEventFeedProjection.ReplaceAllCallCount, "Concurrent event feed refreshes should serialize projection rewrites into a single ReplaceAll call.", failures);
Expect.True(concurrentEventFeedRefreshResults[0].HasRebuilt, "The first concurrent event feed refresh should still rebuild the projection.", failures);
Expect.False(concurrentEventFeedRefreshResults[1].HasRebuilt, "The second concurrent event feed refresh should observe the updated fingerprint and skip the rewrite.", failures);

var searchIndex = new InMemorySearchDocumentIndex();
var searchIndexingService = new SearchIndexingService(searchIndex);
var searchQueryService = new SearchQueryService(searchIndex);

await searchIndexingService.ReplaceAllAsync(
[
    new SearchSourceDocument(
        $"bill:{listedBills[0].Id}",
        listedBills[0].Title,
        SearchDocumentKind.Bill,
        $"Projekt {listedBills[0].ExternalId} ze zrodla {listedBills[0].SourceSystem}.",
        [listedBills[0].ExternalId, .. listedBills[0].DocumentKinds]),
    new SearchSourceDocument(
        $"profile:{profiles[1].Id}",
        profiles[1].Name,
        SearchDocumentKind.Profile,
        $"Profil monitoringu z polityka {profiles[1].AlertPolicy}.",
        profiles[1].Keywords),
    new SearchSourceDocument(
        $"process:{listedProcesses[0].Id}",
        listedProcesses[0].BillTitle,
        SearchDocumentKind.Process,
        $"Proces legislacyjny. Biezacy etap: {listedProcesses[0].CurrentStageLabel} ({listedProcesses[0].CurrentStageCode}).",
        [listedProcesses[0].BillExternalId, listedProcesses[0].CurrentStageCode, listedProcesses[0].CurrentStageLabel]),
    new SearchSourceDocument(
        $"act:{listedActs[0].Id}",
        listedActs[0].Title,
        SearchDocumentKind.Act,
        $"Opublikowany akt prawny. ELI: {listedActs[0].Eli}. Artefakty: {string.Join(", ", listedActs[0].ArtifactKinds)}.",
        [listedActs[0].BillExternalId, listedActs[0].Eli, .. listedActs[0].ArtifactKinds]),
    new SearchSourceDocument(
        $"alert:{alerts[0].Id}",
        alerts[0].BillTitle,
        SearchDocumentKind.Alert,
        $"Alert dla profilu {alerts[0].ProfileName}.",
        alerts[0].MatchedKeywords)
], CancellationToken.None);

var searchResults = await searchQueryService.SearchAsync("VAT", CancellationToken.None);
var searchHits = searchResults.Hits.ToArray();

Expect.Equal("VAT", searchResults.Query, "Search query service should preserve the search text in the result contract.", failures);
Expect.Equal(4, searchHits.Length, "Search query service should return matching bills, acts, profiles and alerts from the index.", failures);
Expect.Equal("bill", searchHits[0].Type, "Search query service should rank bill title matches ahead of other hit types.", failures);
Expect.Equal("Ustawa o zmianie VAT", searchHits[0].Title, "Search query service should surface the best-ranked bill hit first.", failures);
Expect.Equal("act", searchHits[1].Type, "Search query service should rank matching published acts after stronger bill hits.", failures);
Expect.Equal("profile", searchHits[2].Type, "Search query service should return matching profiles after stronger bill and act hits.", failures);
Expect.Equal("alert", searchHits[3].Type, "Search query service should still return matching alerts for the same query.", failures);

var durableSearchRoot = Path.Combine(durableStateRoot, "search");
var durableSearchIndex = new FileBackedSearchDocumentIndex(durableSearchRoot);
var durableSearchIndexingService = new SearchIndexingService(durableSearchIndex);

await durableSearchIndexingService.ReplaceAllAsync(
[
    new SearchSourceDocument(
        $"bill:{listedBills[0].Id}",
        listedBills[0].Title,
        SearchDocumentKind.Bill,
        $"Projekt {listedBills[0].ExternalId} ze zrodla {listedBills[0].SourceSystem}.",
        [listedBills[0].ExternalId, .. listedBills[0].DocumentKinds]),
    new SearchSourceDocument(
        $"profile:{profiles[1].Id}",
        profiles[1].Name,
        SearchDocumentKind.Profile,
        $"Profil monitoringu z polityka {profiles[1].AlertPolicy}.",
        profiles[1].Keywords),
    new SearchSourceDocument(
        $"process:{listedProcesses[0].Id}",
        listedProcesses[0].BillTitle,
        SearchDocumentKind.Process,
        $"Proces legislacyjny. Biezacy etap: {listedProcesses[0].CurrentStageLabel} ({listedProcesses[0].CurrentStageCode}).",
        [listedProcesses[0].BillExternalId, listedProcesses[0].CurrentStageCode, listedProcesses[0].CurrentStageLabel]),
    new SearchSourceDocument(
        $"act:{listedActs[0].Id}",
        listedActs[0].Title,
        SearchDocumentKind.Act,
        $"Opublikowany akt prawny. ELI: {listedActs[0].Eli}. Artefakty: {string.Join(", ", listedActs[0].ArtifactKinds)}.",
        [listedActs[0].BillExternalId, listedActs[0].Eli, .. listedActs[0].ArtifactKinds])
], CancellationToken.None);

await durableSearchIndex.IndexAsync(
    $"alert:{alerts[0].Id}",
    alerts[0].BillTitle,
    $"Alert dla profilu {alerts[0].ProfileName}. {string.Join(", ", alerts[0].MatchedKeywords)}",
    CancellationToken.None);

var durableSearchQueryService = new SearchQueryService(new FileBackedSearchDocumentIndex(durableSearchRoot));
var durableSearchHits = (await durableSearchQueryService.SearchAsync("VAT", CancellationToken.None)).Hits.ToArray();

Expect.Equal(4, durableSearchHits.Length, "File-backed search index should preserve replaced and incrementally indexed documents across store reload.", failures);
Expect.Equal("bill", durableSearchHits[0].Type, "File-backed search index should preserve ranking for strong bill matches after reload.", failures);
Expect.Equal("act", durableSearchHits[1].Type, "File-backed search index should preserve published act hits after reload.", failures);
Expect.Equal("profile", durableSearchHits[2].Type, "File-backed search index should preserve matching profile hits after reload.", failures);
Expect.Equal("alert", durableSearchHits[3].Type, "File-backed search index should preserve incrementally indexed alert hits after reload.", failures);

await durableSearchIndex.RemoveAsync($"alert:{alerts[0].Id}", CancellationToken.None);
var durableSearchHitsAfterRemoval = (await durableSearchQueryService.SearchAsync("VAT", CancellationToken.None)).Hits.ToArray();

Expect.Equal(3, durableSearchHitsAfterRemoval.Length, "File-backed search index should remove deleted documents from subsequent queries across store reload.", failures);
Expect.True(
    durableSearchHitsAfterRemoval.All(hit => !string.Equals(hit.Type, "alert", StringComparison.OrdinalIgnoreCase)),
    "File-backed search index should no longer return removed alert documents after reload.",
    failures);

var aiRepository = new InMemoryAiEnrichmentTaskRepository();
var aiProjection = new InMemoryAiEnrichmentTaskProjectionStore();
var aiCommandService = new AiEnrichmentCommandService(aiRepository, aiProjection);
var aiExecutionService = new AiEnrichmentExecutionService(aiRepository, aiProjection, new OnDemandLocalLlmService(), new PassthroughAiPromptAugmentor());
var aiTasksQueryService = new AiEnrichmentTasksQueryService(aiProjection);

await aiCommandService.RequestAsync(new RequestAiEnrichmentCommand(
    Guid.Parse("F6744E80-95F5-4E03-9AB6-071D9B7B52F3"),
    "bill-summary",
    "bill",
    listedBills[1].Id,
    listedBills[1].Title,
    $"Podsumuj projekt ustawy \"{listedBills[1].Title}\". Zrodlo: {listedBills[1].SourceUrl}"), CancellationToken.None);
await aiCommandService.RequestAsync(new RequestAiEnrichmentCommand(
    Guid.Parse("A555FF2D-1265-4D98-BF10-9018D46D0C6D"),
    "act-summary",
    "act",
    listedActs[0].Id,
    listedActs[0].Title,
    $"Podsumuj opublikowany akt \"{listedActs[0].Title}\". Zrodlo: {listedActs[0].Eli}"), CancellationToken.None);

var aiProcessingResult = await aiExecutionService.ProcessNextQueuedAsync(CancellationToken.None);
var aiTasks = await aiTasksQueryService.GetTasksAsync(CancellationToken.None);
var completedTask = aiTasks.Single(task => task.Status == "completed");
var queuedTask = aiTasks.Single(task => task.Status == "queued");

Expect.True(aiProcessingResult.HasProcessedTask, "AI execution service should process one queued enrichment task when work is available.", failures);
Expect.Equal("completed", aiProcessingResult.Status ?? string.Empty, "AI execution service should report a completed status after a successful local LLM run.", failures);
Expect.Equal(2, aiTasks.Count, "AI tasks query service should expose both queued and completed enrichments.", failures);
Expect.Equal("llama3.2:1b", completedTask.Model ?? string.Empty, "Completed AI enrichment should preserve the local model identifier in the read model.", failures);
Expect.True((completedTask.Content ?? string.Empty).Contains(listedBills[1].Title, StringComparison.OrdinalIgnoreCase), "Completed AI enrichment should contain the summarized subject title.", failures);
Expect.SequenceEqual([listedBills[1].SourceUrl], completedTask.Citations, "Completed AI enrichment should extract source citations from the local prompt.", failures);
Expect.Equal("act", queuedTask.SubjectType, "AI query service should leave unprocessed tasks queued for later worker execution.", failures);

var groundedAiDocumentsRoot = Path.Combine(durableStateRoot, "ai-documents");
var groundedDocumentStore = new LocalFileDocumentStore(groundedAiDocumentsRoot);
var groundedArtifactKey = "acts/DU/2026/501/text.pdf";
await using (var groundedArtifactContent = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Dokument zrodlowy CIT przewiduje wejscie w zycie 1 kwietnia 2026 roku oraz korekty rozliczen zaliczek."), writable: false))
{
    await groundedDocumentStore.PutAsync(
        new DocumentWriteRequest(
            LegalCorpusArtifactStorage.Bucket,
            groundedArtifactKey,
            LegalCorpusArtifactStorage.GuessContentType(groundedArtifactKey),
            groundedArtifactContent),
        CancellationToken.None);
}

var groundedAiRepository = new InMemoryAiEnrichmentTaskRepository();
var groundedAiProjection = new InMemoryAiEnrichmentTaskProjectionStore();
var groundedAiCommandService = new AiEnrichmentCommandService(groundedAiRepository, groundedAiProjection);
var groundedPromptAugmentor = new PublishedActAiPromptAugmentor(
    actRepository,
    new PlainTextOcrService(groundedDocumentStore));
var groundedAugmentation = await groundedPromptAugmentor.AugmentAsync(
    AiTaskSubject.Create(
        "act",
        Guid.Parse("4923A708-2B89-4F17-93A9-0AA3A59C2056"),
        "Ustawa z dnia 28 marca 2026 r. o zmianie ustawy o CIT"),
    "Podsumuj opublikowany akt i uwzglednij material zrodlowy.",
    CancellationToken.None);
var groundedAiExecutionService = new AiEnrichmentExecutionService(
    groundedAiRepository,
    groundedAiProjection,
    new OnDemandLocalLlmService(),
    groundedPromptAugmentor);
var groundedAiQueryService = new AiEnrichmentTasksQueryService(groundedAiProjection);

await groundedAiCommandService.RequestAsync(new RequestAiEnrichmentCommand(
    Guid.Parse("65175E86-C0B4-4279-9A38-6D55699E3A1F"),
    "act-summary",
    "act",
    Guid.Parse("4923A708-2B89-4F17-93A9-0AA3A59C2056"),
    "Ustawa z dnia 28 marca 2026 r. o zmianie ustawy o CIT",
    "Podsumuj opublikowany akt i uwzglednij material zrodlowy."), CancellationToken.None);

var groundedAiResult = await groundedAiExecutionService.ProcessNextQueuedAsync(CancellationToken.None);
var groundedAiTasks = await groundedAiQueryService.GetTasksAsync(CancellationToken.None);
var groundedCompletedTask = groundedAiTasks.Single();

Expect.True(groundedAiResult.HasProcessedTask, "AI execution service should process act tasks grounded in stored source artifacts.", failures);
Expect.Equal("completed", groundedCompletedTask.Status, "Grounded act enrichment should complete when the source artifact is available in the document store.", failures);
Expect.True(
    groundedAugmentation.Prompt.Contains("Dokument zrodlowy CIT", StringComparison.OrdinalIgnoreCase),
    "Published act prompt augmentation should inject OCR-derived source text before local LLM execution.",
    failures);
Expect.True(
    groundedCompletedTask.Citations.Contains("https://eli.gov.pl/eli/DU/2026/501/ogl", StringComparer.OrdinalIgnoreCase),
    "Grounded act enrichment should cite the ELI of the published act.",
    failures);
Expect.True(
    groundedCompletedTask.Citations.Contains("document://legal-corpus/acts/DU/2026/501/text.pdf", StringComparer.OrdinalIgnoreCase),
    "Grounded act enrichment should cite the stored act artifact reference.",
    failures);

var batchedAiRepository = new InMemoryAiEnrichmentTaskRepository();
var batchedAiProjection = new InMemoryAiEnrichmentTaskProjectionStore();
var batchedAiCommandService = new AiEnrichmentCommandService(batchedAiRepository, batchedAiProjection);
var batchedAiExecutionService = new AiEnrichmentExecutionService(batchedAiRepository, batchedAiProjection, new OnDemandLocalLlmService(), new PassthroughAiPromptAugmentor());
var batchedAiQueueProcessor = new AiEnrichmentQueueProcessor(batchedAiRepository, batchedAiExecutionService);
var batchedAiQueryService = new AiEnrichmentTasksQueryService(batchedAiProjection);

await batchedAiCommandService.RequestAsync(new RequestAiEnrichmentCommand(
    Guid.Parse("75BFEAE1-84E3-4C36-8992-248580C4EAD5"),
    "bill-summary",
    "bill",
    listedBills[0].Id,
    listedBills[0].Title,
    $"Podsumuj projekt ustawy \"{listedBills[0].Title}\". Zrodlo: {listedBills[0].SourceUrl}"), CancellationToken.None);
await batchedAiCommandService.RequestAsync(new RequestAiEnrichmentCommand(
    Guid.Parse("8D31E1DE-D17F-4D2D-AB92-4D7AFC2E98D6"),
    "act-summary",
    "act",
    listedActs[1].Id,
    listedActs[1].Title,
    $"Podsumuj opublikowany akt \"{listedActs[1].Title}\". Zrodlo: {listedActs[1].Eli}"), CancellationToken.None);

var aiBatchResult = await batchedAiQueueProcessor.ProcessAvailableAsync(maxTasks: 2, CancellationToken.None);
var processedAiTasks = await batchedAiQueryService.GetTasksAsync(CancellationToken.None);

Expect.Equal(2, aiBatchResult.ProcessedCount, "AI queue processor should process up to the requested batch size of queued tasks.", failures);
Expect.False(aiBatchResult.HasRemainingQueuedTasks, "AI queue processor should report an empty queue after processing all queued tasks in the batch.", failures);
Expect.True(processedAiTasks.All(task => task.Status == "completed"), "AI queue processor should leave processed tasks in the completed state.", failures);

var brokeredAiRepository = new InMemoryAiEnrichmentTaskRepository();
var brokeredAiProjection = new InMemoryAiEnrichmentTaskProjectionStore();
var brokeredAiCommandService = new AiEnrichmentCommandService(brokeredAiRepository, brokeredAiProjection);
var brokeredAiExecutionService = new AiEnrichmentExecutionService(
    brokeredAiRepository,
    brokeredAiProjection,
    new OnDemandLocalLlmService(),
    new PassthroughAiPromptAugmentor());
var brokeredOutboxStore = new InMemoryOutboxMessageStore();
var brokeredPublisher = new RecordingIntegrationEventPublisher();
var brokeredInboxStore = new InMemoryInboxStore();
var aiOutboxPublisher = new AiEnrichmentRequestedOutboxPublisher(brokeredOutboxStore, brokeredPublisher);
var aiMessageHandler = new AiEnrichmentRequestedMessageHandler(brokeredAiExecutionService, brokeredInboxStore);

await brokeredAiCommandService.RequestAsync(new RequestAiEnrichmentCommand(
    Guid.Parse("CCB3DFF7-44EA-4574-A104-29BF6D76D584"),
    "bill-summary",
    "bill",
    listedBills[0].Id,
    listedBills[0].Title,
    $"Podsumuj projekt ustawy \"{listedBills[0].Title}\". Zrodlo: {listedBills[0].SourceUrl}"), CancellationToken.None);
await brokeredOutboxStore.EnqueueAsync(new AiEnrichmentRequestedIntegrationEvent(
    Guid.Parse("9C5237BB-C3D4-422B-B8EF-4E5BAED09B08"),
    DateTimeOffset.UtcNow,
    Guid.Parse("CCB3DFF7-44EA-4574-A104-29BF6D76D584"),
    "bill-summary",
    "bill",
    listedBills[0].Id,
    listedBills[0].Title), CancellationToken.None);

var aiOutboxPublishResult = await aiOutboxPublisher.PublishPendingAsync(maxMessages: 1, CancellationToken.None);
var publishedAiEvent = brokeredPublisher.PublishedEvents.OfType<AiEnrichmentRequestedIntegrationEvent>().Single();
var firstBrokerHandleResult = await aiMessageHandler.HandleAsync(publishedAiEvent, CancellationToken.None);
var secondBrokerHandleResult = await aiMessageHandler.HandleAsync(publishedAiEvent, CancellationToken.None);
var brokeredAiTasks = await new AiEnrichmentTasksQueryService(brokeredAiProjection).GetTasksAsync(CancellationToken.None);
var brokeredCompletedTask = brokeredAiTasks.Single(task => task.Id == Guid.Parse("CCB3DFF7-44EA-4574-A104-29BF6D76D584"));

Expect.Equal(1, aiOutboxPublishResult.PublishedCount, "AI outbox publisher should publish one pending AI integration event to the broker port.", failures);
Expect.False(aiOutboxPublishResult.HasRemainingMessages, "AI outbox publisher should report no remaining AI integration events after publishing the pending batch.", failures);
Expect.Equal(1, brokeredPublisher.PublishedEvents.Count, "AI outbox publisher should pass the integration event to the broker port exactly once.", failures);
Expect.Equal(1, brokeredOutboxStore.PublishedMessageIds.Count, "AI outbox publisher should mark the published outbox message as published.", failures);
Expect.True(firstBrokerHandleResult.HasProcessedTask, "AI broker handler should process a broker-delivered task when it has not been handled before.", failures);
Expect.Equal("completed", firstBrokerHandleResult.Status ?? string.Empty, "AI broker handler should complete the queued task through the application execution service.", failures);
Expect.False(secondBrokerHandleResult.HasProcessedTask, "AI broker handler should stay idempotent for already processed broker messages.", failures);
Expect.Equal(1, brokeredInboxStore.ProcessedMessages.Count, "AI broker handler should record inbox idempotency for processed broker messages.", failures);
Expect.Equal("completed", brokeredCompletedTask.Status, "AI broker handler should leave the task in the completed state after broker delivery.", failures);

var durableReplayRoot = Path.Combine(durableStateRoot, "integration-api", "replays");
var durableReplayRepository = new FileBackedReplayRequestRepository(durableReplayRoot);
var durableReplayProjection = new FileBackedReplayRequestProjectionStore(durableReplayRoot);
var durableReplayCommandService = new ReplayRequestsCommandService(durableReplayRepository, durableReplayProjection);
await durableReplayCommandService.RequestAsync(new RequestReplayCommand(
    Guid.Parse("A8AF4E25-21D6-4824-B289-D04B1ABBE3E6"),
    ReplayScope.Of("persistence-replay"),
    "specs"), CancellationToken.None);

var replayReloadedRepository = new FileBackedReplayRequestRepository(durableReplayRoot);
var replayReloadedProjection = new FileBackedReplayRequestProjectionStore(durableReplayRoot);
var replayReloadedQueryService = new ReplayRequestsQueryService(replayReloadedProjection);
var replayBeforeExecution = await replayReloadedQueryService.GetReplaysAsync(CancellationToken.None);
var replayReloadedExecutionService = new ReplayExecutionService(replayReloadedRepository, replayReloadedProjection);
await replayReloadedExecutionService.ProcessNextQueuedAsync(CancellationToken.None);

var replayFinalProjection = new FileBackedReplayRequestProjectionStore(durableReplayRoot);
var replayFinalQueryService = new ReplayRequestsQueryService(replayFinalProjection);
var replayAfterRestart = await replayFinalQueryService.GetReplaysAsync(CancellationToken.None);

Expect.Equal(1, replayBeforeExecution.Count, "File-backed replay projection should restore queued replay requests after repository restart.", failures);
Expect.Equal("queued", replayBeforeExecution[0].Status, "File-backed replay projection should preserve queued status before execution resumes.", failures);
Expect.Equal(1, replayAfterRestart.Count, "File-backed replay projection should expose the persisted replay request after processing and restart.", failures);
Expect.Equal("completed", replayAfterRestart[0].Status, "File-backed replay execution should persist completed status across projection reloads.", failures);
Expect.Equal("persistence-replay", replayAfterRestart[0].Scope, "File-backed replay projection should preserve the replay scope across restarts.", failures);

var durableBackfillRoot = Path.Combine(durableStateRoot, "integration-api", "backfills");
var durableBackfillRepository = new FileBackedBackfillRequestRepository(durableBackfillRoot);
var durableBackfillProjection = new FileBackedBackfillRequestProjectionStore(durableBackfillRoot);
var durableBackfillCommandService = new BackfillRequestsCommandService(durableBackfillRepository, durableBackfillProjection);
await durableBackfillCommandService.RequestAsync(new RequestBackfillCommand(
    Guid.Parse("9319E24B-529B-4CFB-B2C0-67B9597F8496"),
    BackfillSource.Of("eli"),
    BackfillScope.Of("durable-acts"),
    new DateOnly(2026, 01, 01),
    new DateOnly(2026, 03, 25),
    "specs"), CancellationToken.None);

var backfillReloadedRepository = new FileBackedBackfillRequestRepository(durableBackfillRoot);
var backfillReloadedProjection = new FileBackedBackfillRequestProjectionStore(durableBackfillRoot);
var backfillReloadedQueryService = new BackfillRequestsQueryService(backfillReloadedProjection);
var backfillBeforeExecution = await backfillReloadedQueryService.GetBackfillsAsync(CancellationToken.None);
var backfillReloadedExecutionService = new BackfillExecutionService(backfillReloadedRepository, backfillReloadedProjection);
await backfillReloadedExecutionService.ProcessNextQueuedAsync(CancellationToken.None);

var backfillFinalProjection = new FileBackedBackfillRequestProjectionStore(durableBackfillRoot);
var backfillFinalQueryService = new BackfillRequestsQueryService(backfillFinalProjection);
var backfillAfterRestart = await backfillFinalQueryService.GetBackfillsAsync(CancellationToken.None);

Expect.Equal(1, backfillBeforeExecution.Count, "File-backed backfill projection should restore queued backfill requests after repository restart.", failures);
Expect.Equal("queued", backfillBeforeExecution[0].Status, "File-backed backfill projection should preserve queued status before execution resumes.", failures);
Expect.Equal(1, backfillAfterRestart.Count, "File-backed backfill projection should expose the persisted backfill request after processing and restart.", failures);
Expect.Equal("completed", backfillAfterRestart[0].Status, "File-backed backfill execution should persist completed status across projection reloads.", failures);
Expect.Equal("durable-acts", backfillAfterRestart[0].Scope, "File-backed backfill projection should preserve the backfill scope across restarts.", failures);

var durableAiRoot = Path.Combine(durableStateRoot, "ai-enrichment", "tasks");
var durableAiRepository = new FileBackedAiEnrichmentTaskRepository(durableAiRoot);
var durableAiProjection = new FileBackedAiEnrichmentTaskProjectionStore(durableAiRoot);
var durableAiCommandService = new AiEnrichmentCommandService(durableAiRepository, durableAiProjection);
await durableAiCommandService.RequestAsync(new RequestAiEnrichmentCommand(
    Guid.Parse("D61D35A0-13B8-4EF8-872D-02909E1DAE6E"),
    "bill-summary",
    "bill",
    listedBills[0].Id,
    listedBills[0].Title,
    $"Podsumuj projekt ustawy \"{listedBills[0].Title}\". Zrodlo: {listedBills[0].SourceUrl}"), CancellationToken.None);

var aiReloadedRepository = new FileBackedAiEnrichmentTaskRepository(durableAiRoot);
var aiReloadedProjection = new FileBackedAiEnrichmentTaskProjectionStore(durableAiRoot);
var aiReloadedQueryService = new AiEnrichmentTasksQueryService(aiReloadedProjection);
var aiBeforeExecution = await aiReloadedQueryService.GetTasksAsync(CancellationToken.None);
var aiReloadedExecutionService = new AiEnrichmentExecutionService(aiReloadedRepository, aiReloadedProjection, new OnDemandLocalLlmService(), new PassthroughAiPromptAugmentor());
await aiReloadedExecutionService.ProcessNextQueuedAsync(CancellationToken.None);

var aiFinalProjection = new FileBackedAiEnrichmentTaskProjectionStore(durableAiRoot);
var aiFinalQueryService = new AiEnrichmentTasksQueryService(aiFinalProjection);
var aiAfterRestart = await aiFinalQueryService.GetTasksAsync(CancellationToken.None);

Expect.Equal(1, aiBeforeExecution.Count, "File-backed AI projection should restore queued enrichment tasks after repository restart.", failures);
Expect.Equal("queued", aiBeforeExecution[0].Status, "File-backed AI projection should preserve queued task status before execution resumes.", failures);
Expect.Equal(1, aiAfterRestart.Count, "File-backed AI projection should expose the persisted task after processing and restart.", failures);
Expect.Equal("completed", aiAfterRestart[0].Status, "File-backed AI execution should persist completed status across projection reloads.", failures);
Expect.Equal("llama3.2:1b", aiAfterRestart[0].Model ?? string.Empty, "File-backed AI projection should preserve the local model identifier across restarts.", failures);
Expect.SequenceEqual([listedBills[0].SourceUrl], aiAfterRestart[0].Citations, "File-backed AI projection should preserve source citations across restarts.", failures);

var apiClientRepository = new InMemoryApiClientRepository();
var apiClientProjection = new InMemoryApiClientProjectionStore();
var apiClientCommandService = new ApiClientsCommandService(apiClientRepository, apiClientProjection);
var apiClientsQueryService = new ApiClientsQueryService(apiClientProjection);
var tokenFingerprintService = new Sha256ApiTokenFingerprintService();
var erpExportToken = "erp-export-demo-token";
var portalIntegratorToken = "portal-integrator-demo-token";

await apiClientCommandService.RegisterAsync(new RegisterApiClientCommand(
    Guid.Parse("F3E8F9CA-7345-42CB-B510-F295A5E738B3"),
    "ERP Export",
    "erp-export",
    tokenFingerprintService.CreateFingerprint(erpExportToken),
    ["alerts:read", "alerts:read", "replays:write"]), CancellationToken.None);
await apiClientCommandService.UpdateAsync(new UpdateApiClientCommand(
    Guid.Parse("F3E8F9CA-7345-42CB-B510-F295A5E738B3"),
    "ERP Export Updated",
    tokenFingerprintService.CreateFingerprint("erp-export-rotated-token"),
    ["alerts:read", "webhooks:write", "alerts:read"]), CancellationToken.None);
await apiClientCommandService.DeactivateAsync(new DeactivateApiClientCommand(
    Guid.Parse("F3E8F9CA-7345-42CB-B510-F295A5E738B3")), CancellationToken.None);
await apiClientCommandService.DeactivateAsync(new DeactivateApiClientCommand(
    Guid.Parse("F3E8F9CA-7345-42CB-B510-F295A5E738B3")), CancellationToken.None);
await apiClientCommandService.RegisterAsync(new RegisterApiClientCommand(
    Guid.Parse("532AD21A-FF6D-4665-9F88-6B0295C4D6A2"),
    "Portal Integrator",
    "portal-integrator",
    tokenFingerprintService.CreateFingerprint(portalIntegratorToken),
    ["search:read", "replays:write", "backfills:write", "ai:write", "webhooks:write"]), CancellationToken.None);

var persistedApiClient = await apiClientRepository.GetAsync(
    new ApiClientId(Guid.Parse("F3E8F9CA-7345-42CB-B510-F295A5E738B3")),
    CancellationToken.None);
var apiClients = await apiClientsQueryService.GetApiClientsAsync(CancellationToken.None);
var apiClientAccessService = new ApiClientAccessService(apiClientProjection, tokenFingerprintService);
var portalReplayAccess = await apiClientAccessService.AuthorizeAsync(portalIntegratorToken, "replays:write", CancellationToken.None);
var portalSearchAccess = await apiClientAccessService.AuthorizeAsync(portalIntegratorToken, "search:read", CancellationToken.None);
var portalAiAccess = await apiClientAccessService.AuthorizeAsync(portalIntegratorToken, "ai:write", CancellationToken.None);
var portalWebhookAccess = await apiClientAccessService.AuthorizeAsync(portalIntegratorToken, "webhooks:write", CancellationToken.None);
var inactiveErpAccess = await apiClientAccessService.AuthorizeAsync("erp-export-rotated-token", "webhooks:write", CancellationToken.None);
var staleErpTokenAccess = await apiClientAccessService.AuthorizeAsync(erpExportToken, "alerts:read", CancellationToken.None);
var missingScopeAccess = await apiClientAccessService.AuthorizeAsync(portalIntegratorToken, "alerts:read", CancellationToken.None);
var unknownTokenAccess = await apiClientAccessService.AuthorizeAsync("unknown-demo-token", "replays:write", CancellationToken.None);

Expect.True(persistedApiClient is not null, "API client repository should rehydrate a saved API client aggregate from the event stream.", failures);
Expect.Equal(false, persistedApiClient?.IsActive ?? true, "API client aggregate should support explicit deactivation through domain methods.", failures);
Expect.Equal(2, persistedApiClient?.Scopes.Count ?? 0, "API client aggregate should deduplicate repeated scopes.", failures);
Expect.Equal(3L, persistedApiClient?.Version ?? 0L, "API client aggregate should emit events only for effective lifecycle changes.", failures);
Expect.Equal(2, apiClients.Count, "API clients query service should expose projected machine-to-machine clients after commands complete.", failures);
Expect.Equal("ERP Export Updated", apiClients[0].Name, "API clients query service should expose the updated client name for stable responses.", failures);
Expect.Equal(false, apiClients[0].IsActive, "API clients query service should expose deactivated clients.", failures);
Expect.SequenceEqual(["alerts:read", "webhooks:write"], apiClients[0].Scopes, "API clients query service should preserve updated deduplicated scopes in the contract response.", failures);
Expect.Equal(tokenFingerprintService.CreateFingerprint("erp-export-rotated-token"), apiClients[0].TokenFingerprint, "API clients query service should preserve updated token fingerprint metadata.", failures);
Expect.Equal(ApiClientAccessDecision.Authorized, portalReplayAccess.Decision, "API client access service should authorize active clients with the required replay scope.", failures);
Expect.Equal("portal-integrator", portalReplayAccess.ClientIdentifier ?? string.Empty, "API client access service should expose the authorized client identifier.", failures);
Expect.Equal(ApiClientAccessDecision.Authorized, portalSearchAccess.Decision, "API client access service should authorize active clients for their declared read scopes.", failures);
Expect.Equal(ApiClientAccessDecision.Authorized, portalAiAccess.Decision, "API client access service should authorize active clients for AI write requests when the scope is present.", failures);
Expect.Equal(ApiClientAccessDecision.Authorized, portalWebhookAccess.Decision, "API client access service should authorize active clients for webhook write requests when the scope is present.", failures);
Expect.Equal(ApiClientAccessDecision.InactiveClient, inactiveErpAccess.Decision, "API client access service should reject inactive clients even when the token fingerprint matches.", failures);
Expect.Equal(ApiClientAccessDecision.UnknownToken, staleErpTokenAccess.Decision, "API client access service should reject the stale token after API client secret rotation.", failures);
Expect.Equal(ApiClientAccessDecision.MissingScope, missingScopeAccess.Decision, "API client access service should reject requests for scopes the client does not have.", failures);
Expect.Equal(ApiClientAccessDecision.UnknownToken, unknownTokenAccess.Decision, "API client access service should reject unknown bearer tokens.", failures);

var durableApiClientsRoot = Path.Combine(durableStateRoot, "identity-and-access", "api-clients");
var durableApiClientRepository = new FileBackedApiClientRepository(durableApiClientsRoot);
var durableApiClientProjection = new FileBackedApiClientProjectionStore(durableApiClientsRoot);
var durableApiClientCommandService = new ApiClientsCommandService(durableApiClientRepository, durableApiClientProjection);

await durableApiClientCommandService.RegisterAsync(new RegisterApiClientCommand(
    Guid.Parse("AAA20B80-0A49-43F3-890B-CA85EED11D2A"),
    "Durable Integrator",
    "durable-integrator",
    tokenFingerprintService.CreateFingerprint("durable-integrator-token"),
    ["search:read", "replays:write"]), CancellationToken.None);
await durableApiClientCommandService.UpdateAsync(new UpdateApiClientCommand(
    Guid.Parse("AAA20B80-0A49-43F3-890B-CA85EED11D2A"),
    "Durable Integrator Updated",
    tokenFingerprintService.CreateFingerprint("durable-integrator-token-rotated"),
    ["ai:write", "search:read"]), CancellationToken.None);
await durableApiClientCommandService.DeactivateAsync(new DeactivateApiClientCommand(
    Guid.Parse("AAA20B80-0A49-43F3-890B-CA85EED11D2A")), CancellationToken.None);
await durableApiClientCommandService.RegisterAsync(new RegisterApiClientCommand(
    Guid.Parse("D5CFBB5C-A5F2-4419-A4D9-B7E7B281F95B"),
    "Durable Search Client",
    "durable-search",
    tokenFingerprintService.CreateFingerprint("durable-search-token"),
    ["search:read", "ai:write"]), CancellationToken.None);

var durableReloadedApiClientRepository = new FileBackedApiClientRepository(durableApiClientsRoot);
var durableReloadedApiClientProjection = new FileBackedApiClientProjectionStore(durableApiClientsRoot);
var durablePersistedApiClient = await durableReloadedApiClientRepository.GetAsync(
    new ApiClientId(Guid.Parse("AAA20B80-0A49-43F3-890B-CA85EED11D2A")),
    CancellationToken.None);
var durableApiClients = await new ApiClientsQueryService(durableReloadedApiClientProjection).GetApiClientsAsync(CancellationToken.None);
var durableAccessService = new ApiClientAccessService(durableReloadedApiClientProjection, tokenFingerprintService);
var durableAuthorizedAccess = await durableAccessService.AuthorizeAsync("durable-search-token", "ai:write", CancellationToken.None);
var durableInactiveAccess = await durableAccessService.AuthorizeAsync("durable-integrator-token-rotated", "ai:write", CancellationToken.None);
var durableStaleAccess = await durableAccessService.AuthorizeAsync("durable-integrator-token", "search:read", CancellationToken.None);

Expect.True(durablePersistedApiClient is not null, "File-backed API client repository should rehydrate a saved API client aggregate after store reload.", failures);
Expect.Equal(false, durablePersistedApiClient?.IsActive ?? true, "File-backed API client aggregate should preserve deactivation across reload.", failures);
Expect.Equal(2, durableApiClients.Count, "File-backed API client projection should preserve registered clients across reload.", failures);
Expect.Equal("Durable Integrator Updated", durableApiClients[0].Name, "File-backed API client query should expose updated names after reload.", failures);
Expect.SequenceEqual(["ai:write", "search:read"], durableApiClients[0].Scopes, "File-backed API client query should preserve updated scopes after reload.", failures);
Expect.Equal(tokenFingerprintService.CreateFingerprint("durable-integrator-token-rotated"), durableApiClients[0].TokenFingerprint, "File-backed API client query should preserve updated token fingerprints after reload.", failures);
Expect.Equal(ApiClientAccessDecision.Authorized, durableAuthorizedAccess.Decision, "File-backed API client projection should authorize active clients after reload.", failures);
Expect.Equal(ApiClientAccessDecision.InactiveClient, durableInactiveAccess.Decision, "File-backed API client projection should preserve inactive clients after reload.", failures);
Expect.Equal(ApiClientAccessDecision.UnknownToken, durableStaleAccess.Decision, "File-backed API client projection should reject stale tokens after reload.", failures);

var operatorPasswordHasher = new Pbkdf2OperatorPasswordHasher();
var operatorRepository = new InMemoryOperatorAccountRepository();
var operatorProjection = new InMemoryOperatorAccountProjectionStore();
var operatorCommandService = new OperatorAccountsCommandService(operatorRepository, operatorProjection, operatorProjection, operatorPasswordHasher);
var operatorQueryService = new OperatorAccountsQueryService(operatorProjection);
var operatorAuthenticationService = new OperatorAuthenticationService(operatorProjection, operatorPasswordHasher);
var operatorAccessService = new OperatorAccessService(operatorProjection);

var adminOperatorId = Guid.Parse("A0677E75-A491-4B61-8465-2BF152F4571D");
var analystOperatorId = Guid.Parse("32D9E4D6-2A54-4383-A35B-2BE68308F7A9");

await operatorCommandService.RegisterAsync(new RegisterOperatorAccountCommand(
    adminOperatorId,
    "admin@lawwatcher.local",
    "Local Admin",
    "Admin123!",
    ["operators:write", "profiles:write"]), CancellationToken.None);
await operatorCommandService.RegisterAsync(new RegisterOperatorAccountCommand(
    analystOperatorId,
    "analyst@lawwatcher.local",
    "Analyst",
    "Analyst123!",
    ["profiles:write"]), CancellationToken.None);
await operatorCommandService.UpdateAsync(new UpdateOperatorAccountCommand(
    adminOperatorId,
    "Operations Admin",
    ["operators:write", "profiles:write", "subscriptions:write"]), CancellationToken.None);
await operatorCommandService.ResetPasswordAsync(new ResetOperatorPasswordCommand(
    adminOperatorId,
    "Admin456!"), CancellationToken.None);
await operatorCommandService.DeactivateAsync(new DeactivateOperatorAccountCommand(
    analystOperatorId), CancellationToken.None);

var persistedOperator = await operatorRepository.GetAsync(new OperatorAccountId(adminOperatorId), CancellationToken.None);
var operatorAccounts = await operatorQueryService.GetOperatorsAsync(CancellationToken.None);
var operatorAuthBeforePasswordReset = await operatorAuthenticationService.AuthenticateAsync(
    "admin@lawwatcher.local",
    "Admin123!",
    CancellationToken.None);
var operatorAuthAfterPasswordReset = await operatorAuthenticationService.AuthenticateAsync(
    "admin@lawwatcher.local",
    "Admin456!",
    CancellationToken.None);
var inactiveOperatorAuth = await operatorAuthenticationService.AuthenticateAsync(
    "analyst@lawwatcher.local",
    "Analyst123!",
    CancellationToken.None);
var missingOperatorAuth = await operatorAuthenticationService.AuthenticateAsync(
    "missing@lawwatcher.local",
    "Analyst123!",
    CancellationToken.None);
var operatorAuthorizedAccess = await operatorAccessService.AuthorizeAsync(
    adminOperatorId,
    "subscriptions:write",
    CancellationToken.None);
var operatorMissingPermissionAccess = await operatorAccessService.AuthorizeAsync(
    adminOperatorId,
    "webhooks:write",
    CancellationToken.None);
var inactiveOperatorAccess = await operatorAccessService.AuthorizeAsync(
    analystOperatorId,
    "profiles:write",
    CancellationToken.None);

Expect.True(persistedOperator is not null, "Operator account repository should rehydrate a saved operator aggregate from the event stream.", failures);
Expect.Equal("Operations Admin", persistedOperator?.DisplayName.Value ?? string.Empty, "Operator account aggregate should preserve the latest display name after updates.", failures);
Expect.Equal(3, persistedOperator?.Permissions.Count ?? 0, "Operator account aggregate should preserve the latest deduplicated permissions after updates.", failures);
Expect.Equal(3L, persistedOperator?.Version ?? 0L, "Operator account aggregate should emit events only for effective register, update and password-reset changes.", failures);
Expect.True(operatorPasswordHasher.Verify("Admin456!", persistedOperator?.PasswordHash.Value ?? string.Empty), "Operator password hasher should verify persisted password hashes after password reset.", failures);
Expect.Equal(2, operatorAccounts.Count, "Operator accounts query service should expose projected operators after commands complete.", failures);
Expect.Equal("admin@lawwatcher.local", operatorAccounts[0].Email, "Operator accounts query service should sort operators by email for stable responses.", failures);
Expect.SequenceEqual(["operators:write", "profiles:write", "subscriptions:write"], operatorAccounts[0].Permissions, "Operator accounts query service should preserve deduplicated permissions in sorted order.", failures);
Expect.Equal("analyst@lawwatcher.local", operatorAccounts[1].Email, "Operator accounts query service should keep later email entries after the sorted prefix.", failures);
Expect.Equal(false, operatorAccounts[1].IsActive, "Operator accounts query service should expose deactivated operators.", failures);
Expect.Equal(OperatorAuthenticationDecision.InvalidPassword, operatorAuthBeforePasswordReset.Decision, "Operator authentication should reject stale passwords after a password reset.", failures);
Expect.Equal(OperatorAuthenticationDecision.Authorized, operatorAuthAfterPasswordReset.Decision, "Operator authentication should authorize active operators with a valid password hash.", failures);
Expect.Equal("admin@lawwatcher.local", operatorAuthAfterPasswordReset.Email ?? string.Empty, "Operator authentication should expose the authenticated operator email.", failures);
Expect.Equal(OperatorAuthenticationDecision.InactiveOperator, inactiveOperatorAuth.Decision, "Operator authentication should reject inactive operators even when the password is valid.", failures);
Expect.Equal(OperatorAuthenticationDecision.UnknownEmail, missingOperatorAuth.Decision, "Operator authentication should reject unknown operator emails.", failures);
Expect.Equal(OperatorAccessDecision.Authorized, operatorAuthorizedAccess.Decision, "Operator access service should authorize active operators with the required permission.", failures);
Expect.Equal(OperatorAccessDecision.MissingPermission, operatorMissingPermissionAccess.Decision, "Operator access service should reject missing permissions.", failures);
Expect.Equal(OperatorAccessDecision.InactiveOperator, inactiveOperatorAccess.Decision, "Operator access service should reject inactive operators.", failures);

var durableOperatorAccountsRoot = Path.Combine(durableStateRoot, "identity-and-access", "operator-accounts");
var durableOperatorRepository = new FileBackedOperatorAccountRepository(durableOperatorAccountsRoot);
var durableOperatorProjection = new FileBackedOperatorAccountProjectionStore(durableOperatorAccountsRoot);
var durableOperatorCommandService = new OperatorAccountsCommandService(
    durableOperatorRepository,
    durableOperatorProjection,
    durableOperatorProjection,
    operatorPasswordHasher);

await durableOperatorCommandService.RegisterAsync(new RegisterOperatorAccountCommand(
    Guid.Parse("6C9291BB-0A6E-4AEA-A4CC-1A155EF7A93B"),
    "durable.admin@lawwatcher.local",
    "Durable Admin",
    "Durable123!",
    ["operators:write", "webhooks:write"]), CancellationToken.None);
await durableOperatorCommandService.DeactivateAsync(new DeactivateOperatorAccountCommand(
    Guid.Parse("6C9291BB-0A6E-4AEA-A4CC-1A155EF7A93B")), CancellationToken.None);
await durableOperatorCommandService.RegisterAsync(new RegisterOperatorAccountCommand(
    Guid.Parse("165AF325-AB9B-49F3-B640-88C750728CB2"),
    "durable.viewer@lawwatcher.local",
    "Durable Viewer",
    "Viewer123!",
    ["profiles:write"]), CancellationToken.None);

var durableReloadedOperatorRepository = new FileBackedOperatorAccountRepository(durableOperatorAccountsRoot);
var durableReloadedOperatorProjection = new FileBackedOperatorAccountProjectionStore(durableOperatorAccountsRoot);
var durablePersistedOperator = await durableReloadedOperatorRepository.GetAsync(
    new OperatorAccountId(Guid.Parse("6C9291BB-0A6E-4AEA-A4CC-1A155EF7A93B")),
    CancellationToken.None);
var durableOperators = await new OperatorAccountsQueryService(durableReloadedOperatorProjection).GetOperatorsAsync(CancellationToken.None);
var durableAuthenticationService = new OperatorAuthenticationService(durableReloadedOperatorProjection, operatorPasswordHasher);
var durableOperatorAccessService = new OperatorAccessService(durableReloadedOperatorProjection);
var durableAuthorizedOperatorAccess = await durableOperatorAccessService.AuthorizeAsync(
    Guid.Parse("165AF325-AB9B-49F3-B640-88C750728CB2"),
    "profiles:write",
    CancellationToken.None);
var durableInactiveOperatorAuthentication = await durableAuthenticationService.AuthenticateAsync(
    "durable.admin@lawwatcher.local",
    "Durable123!",
    CancellationToken.None);

Expect.True(durablePersistedOperator is not null, "File-backed operator repository should rehydrate a saved operator aggregate after store reload.", failures);
Expect.Equal(false, durablePersistedOperator?.IsActive ?? true, "File-backed operator aggregate should preserve deactivation across reload.", failures);
Expect.Equal(2, durableOperators.Count, "File-backed operator projection should preserve registered operators across reload.", failures);
Expect.Equal("durable.admin@lawwatcher.local", durableOperators[0].Email, "File-backed operator query should sort operators by email after reload.", failures);
Expect.Equal(OperatorAccessDecision.Authorized, durableAuthorizedOperatorAccess.Decision, "File-backed operator projection should authorize active operators after reload.", failures);
Expect.Equal(OperatorAuthenticationDecision.InactiveOperator, durableInactiveOperatorAuthentication.Decision, "File-backed operator projection should preserve inactive operators after reload.", failures);

var adapterRoot = Path.Combine(AppContext.BaseDirectory, "spec-artifacts", Guid.NewGuid().ToString("N"));
var documentStore = new LocalFileDocumentStore(adapterRoot);
await using (var content = new MemoryStream(Encoding.UTF8.GetBytes("Art. 1. Zmiana ustawy o VAT.")))
{
    var storedDocument = await documentStore.PutAsync(
        new DocumentWriteRequest(
            "documents",
            "acts/2026/502/text.txt",
            "text/plain",
            content),
        CancellationToken.None);

    await using var openedDocument = await documentStore.OpenReadAsync(storedDocument, CancellationToken.None);
    using var reader = new StreamReader(openedDocument, Encoding.UTF8, leaveOpen: false);
    var reopenedContent = await reader.ReadToEndAsync(CancellationToken.None);

    var ocrService = new PlainTextOcrService(documentStore);
    var ocrResult = await ocrService.ExtractAsync(storedDocument, CancellationToken.None);

    Expect.Equal("documents", storedDocument.Bucket, "Local document store should preserve the requested bucket.", failures);
    Expect.Equal("acts/2026/502/text.txt", storedDocument.ObjectKey, "Local document store should preserve the requested object key.", failures);
    Expect.Equal("Art. 1. Zmiana ustawy o VAT.", reopenedContent, "Local document store should return the bytes that were written.", failures);
    Expect.Equal("Art. 1. Zmiana ustawy o VAT.", ocrResult.ExtractedText, "Plain-text OCR adapter should extract text from stored documents.", failures);
    Expect.Equal(0, ocrResult.Warnings.Count, "Plain-text OCR adapter should not emit warnings for UTF-8 text files.", failures);
}

var embeddingService = new DeterministicEmbeddingService();
var embedding = await embeddingService.GenerateAsync("VAT CIT JPK", CancellationToken.None);

Expect.Equal("lawwatcher-local-embedding-v1", embedding.Model, "Deterministic embedding adapter should expose the local embedding model identifier.", failures);
Expect.Equal(8, embedding.Values.Count, "Deterministic embedding adapter should return a fixed-size vector for stable local indexing.", failures);
Expect.True(embedding.Values.Any(value => value > 0F), "Deterministic embedding adapter should produce non-zero values for non-empty content.", failures);

var ollamaEmbeddingHandler = new StubSequenceHttpMessageHandler(request =>
{
    var requestBody = request.Content is null
        ? string.Empty
        : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();

    if (request.Method != HttpMethod.Post ||
        request.RequestUri?.AbsolutePath != "/api/embed" ||
        !requestBody.Contains("\"model\":\"nomic-embed-text\"", StringComparison.Ordinal) ||
        !requestBody.Contains("\"input\":\"VAT CIT JPK\"", StringComparison.Ordinal))
    {
        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"unexpected request\"}", Encoding.UTF8, "application/json")
        };
    }

    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {
              "model": "nomic-embed-text",
              "embeddings": [
                [0.11, 0.22, 0.33, 0.44]
              ]
            }
            """, Encoding.UTF8, "application/json")
    };
});
var ollamaEmbedding = await new OllamaEmbeddingService(
    new HttpClient(ollamaEmbeddingHandler)
    {
        BaseAddress = new Uri("http://127.0.0.1:11434")
    },
    "nomic-embed-text").GenerateAsync("VAT CIT JPK", CancellationToken.None);

Expect.Equal("nomic-embed-text", ollamaEmbedding.Model, "Ollama embedding adapter should surface the configured embedding model identifier.", failures);
Expect.Equal(4, ollamaEmbedding.Values.Count, "Ollama embedding adapter should deserialize the returned vector size.", failures);
Expect.Equal(0.33f, ollamaEmbedding.Values.ElementAt(2), "Ollama embedding adapter should preserve returned embedding values.", failures);

var incrementalIndex = new InMemorySearchDocumentIndex();
var incrementalSearchIndexer = (ISearchIndexer)incrementalIndex;
var incrementalSearchQueryService = new SearchQueryService(incrementalIndex);

await incrementalSearchIndexer.IndexAsync(
    "bill:vat-2026-1",
    "Ustawa o zmianie VAT",
    "Zmiana obejmuje VAT i JPK.",
    CancellationToken.None);

var incrementalHits = (await incrementalSearchQueryService.SearchAsync("VAT", CancellationToken.None)).Hits.ToArray();

Expect.Equal(1, incrementalHits.Length, "Incremental search indexer should make indexed documents queryable through the read-side search service.", failures);
Expect.Equal("bill", incrementalHits[0].Type, "Incremental search indexer should infer bill kind from the indexed document identifier.", failures);

await incrementalSearchIndexer.RemoveAsync("bill:vat-2026-1", CancellationToken.None);
var hitsAfterRemoval = (await incrementalSearchQueryService.SearchAsync("VAT", CancellationToken.None)).Hits.ToArray();

Expect.Equal(0, hitsAfterRemoval.Length, "Incremental search indexer should remove documents from subsequent search results.", failures);

var webhookDispatcher = new InMemoryWebhookDispatcher();
await webhookDispatcher.DispatchAsync(new WebhookDispatchRequest(
    "https://erp.example.test/lawwatcher",
    "alert.created",
    "{\"alertId\":\"demo\"}",
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["X-LawWatcher-Signature"] = "sha256=test"
    }), CancellationToken.None);
var webhookDispatches = await webhookDispatcher.GetDispatchesAsync(CancellationToken.None);

Expect.Equal(1, webhookDispatches.Count, "Webhook dispatcher adapter should record dispatched webhook requests.", failures);
Expect.Equal("alert.created", webhookDispatches[0].EventType, "Webhook dispatcher adapter should preserve the event type.", failures);
Expect.Equal("sha256=test", webhookDispatches[0].Headers["X-LawWatcher-Signature"], "Webhook dispatcher adapter should preserve outbound signature headers.", failures);

var signedWebhookHandler = new RecordingWebhookMessageHandler();
var signedWebhookDispatcher = new SignedHttpWebhookDispatcher(
    new HttpClient(signedWebhookHandler),
    new WebhookDeliveryOptions
    {
        Backend = "SignedHttp",
        SigningSecret = "spec-secret",
        TimeoutSeconds = 5
    });
await signedWebhookDispatcher.DispatchAsync(new WebhookDispatchRequest(
    "http://127.0.0.1:5310/hooks/alerts",
    "alert.created",
    "{\"alertId\":\"demo\"}",
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["X-LawWatcher-Channel"] = "webhook"
    }), CancellationToken.None);
var signedWebhookRequest = signedWebhookHandler.Request ?? throw new InvalidOperationException("Signed HTTP webhook dispatcher should capture the outbound request.");
var signedWebhookPayload = signedWebhookHandler.Payload ?? string.Empty;
var expectedSignature = ComputeTestWebhookSignature(signedWebhookPayload, "spec-secret");

Expect.Equal(HttpMethod.Post, signedWebhookRequest.Method, "Signed HTTP webhook dispatcher should send POST requests.", failures);
Expect.Equal("http://127.0.0.1:5310/hooks/alerts", signedWebhookRequest.RequestUri?.ToString() ?? string.Empty, "Signed HTTP webhook dispatcher should preserve the callback URL.", failures);
Expect.Equal("{\"alertId\":\"demo\"}", signedWebhookPayload, "Signed HTTP webhook dispatcher should preserve the JSON payload body.", failures);
Expect.Equal("alert.created", string.Join(",", signedWebhookRequest.Headers.GetValues("X-LawWatcher-Event-Type")), "Signed HTTP webhook dispatcher should add the event type header when missing.", failures);
Expect.Equal(expectedSignature, string.Join(",", signedWebhookRequest.Headers.GetValues("X-LawWatcher-Signature")), "Signed HTTP webhook dispatcher should sign the outbound payload with HMAC SHA-256.", failures);
Expect.Equal("webhook", string.Join(",", signedWebhookRequest.Headers.GetValues("X-LawWatcher-Channel")), "Signed HTTP webhook dispatcher should preserve custom outbound headers.", failures);

if (failures.Count != 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"FAIL: {failure}");
    }

    return 1;
}

Console.WriteLine("Application specifications passed.");
return 0;

static string ComputeTestWebhookSignature(string payload, string secret)
{
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    return $"sha256={Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)))}";
}

sealed class RecordingWebhookMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }

    public string? Payload { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        Payload = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("{}")
        };
    }
}

sealed class StubSequenceHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_responder(request));
    }
}

static class Expect
{
    public static void Equal<T>(T expected, T actual, string message, List<string> failures)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            failures.Add($"{message} Expected: {expected}. Actual: {actual}.");
        }
    }

    public static void True(bool condition, string message, List<string> failures)
    {
        if (!condition)
        {
            failures.Add(message);
        }
    }

    public static void False(bool condition, string message, List<string> failures)
    {
        if (condition)
        {
            failures.Add(message);
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message, List<string> failures)
        where T : notnull
    {
        if (!expected.SequenceEqual(actual))
        {
            failures.Add($"{message} Expected: [{string.Join(", ", expected)}]. Actual: [{string.Join(", ", actual)}].");
        }
    }
}

internal sealed class StubMonitoringProfileReadRepository(params MonitoringProfileReadModel[] profiles)
    : IMonitoringProfileReadRepository
{
    private readonly IReadOnlyCollection<MonitoringProfileReadModel> _profiles = profiles;

    public Task<IReadOnlyCollection<MonitoringProfileReadModel>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_profiles);
    }
}

internal sealed class StubEventFeedSource(params EventFeedItem[] items)
    : IEventFeedSource
{
    private readonly IReadOnlyCollection<EventFeedItem> _items = items;

    public Task<IReadOnlyCollection<EventFeedItem>> GetEventsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_items);
    }
}

internal sealed class BlockingEventFeedProjection : IEventFeedProjection
{
    private readonly TaskCompletionSource _firstReplaceStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allowFirstReplaceToComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _replaceAllCallCount;

    public Task FirstReplaceStarted => _firstReplaceStarted.Task;

    public int ReplaceAllCallCount => _replaceAllCallCount;

    public void AllowFirstReplaceToComplete() => _allowFirstReplaceToComplete.TrySetResult();

    public async Task ReplaceAllAsync(IReadOnlyCollection<EventFeedItem> events, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var callCount = Interlocked.Increment(ref _replaceAllCallCount);
        if (callCount == 1)
        {
            _firstReplaceStarted.TrySetResult();
            await _allowFirstReplaceToComplete.Task.WaitAsync(cancellationToken);
        }
    }
}

internal sealed class InMemoryOutboxMessageStore : IOutboxMessageStore
{
    private readonly Dictionary<Guid, OutboxMessage> _messages = [];

    public bool SupportsPolling => true;

    public IReadOnlyCollection<Guid> PublishedMessageIds => _messages.Values
        .Where(message => message.NextAttemptAtUtc is null && !_pendingMessageIds.Contains(message.MessageId))
        .Select(message => message.MessageId)
        .ToArray();

    public IReadOnlyCollection<Guid> DeferredMessageIds => _deferredMessageIds;

    private readonly HashSet<Guid> _pendingMessageIds = [];
    private readonly HashSet<Guid> _deferredMessageIds = [];

    public Task EnqueueAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var messageClrType = integrationEvent.GetType();
        var payload = System.Text.Json.JsonSerializer.Serialize(integrationEvent, messageClrType, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var message = new OutboxMessage(
            integrationEvent.EventId,
            messageClrType.FullName ?? messageClrType.Name,
            payload,
            null,
            0,
            integrationEvent.OccurredAtUtc,
            null);

        _messages[message.MessageId] = message;
        _pendingMessageIds.Add(message.MessageId);
        _deferredMessageIds.Remove(message.MessageId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<OutboxMessage>> GetPendingAsync(IReadOnlyCollection<string> messageTypes, int maxCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var matches = _messages.Values
            .Where(message => _pendingMessageIds.Contains(message.MessageId))
            .Where(message => messageTypes.Contains(message.MessageType, StringComparer.Ordinal))
            .OrderBy(message => message.CreatedAtUtc)
            .Take(maxCount)
            .ToArray();

        return Task.FromResult<IReadOnlyCollection<OutboxMessage>>(matches);
    }

    public Task MarkPublishedAsync(Guid messageId, DateTimeOffset publishedAtUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pendingMessageIds.Remove(messageId);
        _deferredMessageIds.Remove(messageId);
        return Task.CompletedTask;
    }

    public Task DeferAsync(Guid messageId, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_messages.TryGetValue(messageId, out var message))
        {
            _messages[messageId] = message with
            {
                AttemptCount = message.AttemptCount + 1,
                NextAttemptAtUtc = nextAttemptAtUtc
            };
        }

        _deferredMessageIds.Add(messageId);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly List<IIntegrationEvent> _publishedEvents = [];

    public IReadOnlyCollection<IIntegrationEvent> PublishedEvents => _publishedEvents;

    public Task PublishAsync<TIntegrationEvent>(TIntegrationEvent integrationEvent, CancellationToken cancellationToken)
        where TIntegrationEvent : class, IIntegrationEvent
    {
        cancellationToken.ThrowIfCancellationRequested();
        _publishedEvents.Add(integrationEvent);
        return Task.CompletedTask;
    }
}

internal sealed class CountingBillProjectionRefreshOrchestrator : IBillProjectionRefreshOrchestrator
{
    public int InvocationCount { get; private set; }

    public Task<BillProjectionRefreshExecutionResult> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvocationCount++;
        return Task.FromResult(new BillProjectionRefreshExecutionResult(true));
    }
}

internal sealed class CountingProcessProjectionRefreshOrchestrator : IProcessProjectionRefreshOrchestrator
{
    public int InvocationCount { get; private set; }

    public Task<ProcessProjectionRefreshExecutionResult> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvocationCount++;
        return Task.FromResult(new ProcessProjectionRefreshExecutionResult(true));
    }
}

internal sealed class CountingActProjectionRefreshOrchestrator : IActProjectionRefreshOrchestrator
{
    public int InvocationCount { get; private set; }

    public Task<ActProjectionRefreshExecutionResult> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvocationCount++;
        return Task.FromResult(new ActProjectionRefreshExecutionResult(true));
    }
}

internal sealed class CountingMonitoringProfileProjectionRefreshOrchestrator : IMonitoringProfileProjectionRefreshOrchestrator
{
    public int InvocationCount { get; private set; }

    public Task<MonitoringProfileProjectionRefreshExecutionResult> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvocationCount++;
        return Task.FromResult(new MonitoringProfileProjectionRefreshExecutionResult(true));
    }
}

internal sealed class InMemoryInboxStore : IInboxStore
{
    private readonly HashSet<string> _processed = [];

    public IReadOnlyCollection<string> ProcessedMessages => _processed;

    public Task<bool> HasProcessedAsync(Guid messageId, string consumerName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_processed.Contains($"{consumerName}:{messageId:D}"));
    }

    public Task MarkProcessedAsync(Guid messageId, string consumerName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _processed.Add($"{consumerName}:{messageId:D}");
        return Task.CompletedTask;
    }
}

internal sealed class StubMessagingDiagnosticsStore(MessagingDiagnosticsSnapshot snapshot) : IMessagingDiagnosticsStore
{
    public bool IsAvailable => snapshot.IsAvailable;

    public Task<MessagingDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(snapshot);
    }
}

internal sealed class StubRetentionMaintenanceStore(RetentionMaintenanceExecutionResult result) : IRetentionMaintenanceStore
{
    public bool IsAvailable => result.MaintenanceAvailable;

    public Task<RetentionMaintenanceExecutionResult> RunAsync(
        RetentionMaintenancePolicy policy,
        DateTimeOffset executedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result with { ExecutedAtUtc = executedAtUtc });
    }
}
