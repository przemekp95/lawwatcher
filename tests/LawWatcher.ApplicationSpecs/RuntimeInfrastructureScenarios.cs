using LawWatcher.AiEnrichment.Application;
using LawWatcher.AiEnrichment.Contracts;
using LawWatcher.AiEnrichment.Infrastructure;
using LawWatcher.Api.Runtime;
using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.SearchAndDiscovery.Application;
using LawWatcher.SearchAndDiscovery.Contracts;
using LawWatcher.SearchAndDiscovery.Infrastructure;
using System.Net;
using System.Net.Http;
using System.Text;

internal static class RuntimeInfrastructureScenarios
{
    public static async Task RunAsync(List<string> failures)
    {
        var dev = RuntimeProfile.Parse("dev");
        Expect.Equal(RuntimeProfile.Dev, dev, "Runtime profile parser should recognize dev.", failures);
        var production = RuntimeProfile.Parse("production");
        Expect.Equal(RuntimeProfile.Production, production, "Runtime profile parser should recognize production.", failures);
        Expect.True(
            ThrowsUnsupportedRuntimeProfile("dev-laptop"),
            "Runtime profile parser should reject the removed dev-laptop alias from the frozen 1.0 surface.",
            failures);
        Expect.True(
            ThrowsUnsupportedRuntimeProfile("full-host"),
            "Runtime profile parser should reject the removed full-host alias from the frozen 1.0 surface.",
            failures);
        AssertFrozenSupportedSurfaceUsesCanonicalNames(FindRepositoryRoot(), failures);

        var capabilities = SystemCapabilities.FromOptions(
            dev,
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
        Expect.False(capabilities.Search.UseSqlFullText, "Projection index should be the supported baseline search backend on laptop-first profile.", failures);
        Expect.False(capabilities.Search.UseHybridSearch, "Hybrid search should stay disabled on laptop-first profile by default.", failures);

        var selector = new SearchBackendSelector();
        Expect.Equal(SearchBackend.ProjectionIndex, selector.Select(capabilities.Search), "Search backend selector should use the projection index when laptop-first SQL Full-Text is not explicitly enabled.", failures);

        var sqlFullTextCapabilities = SearchCapabilitiesRuntimeResolver.Resolve(
            capabilities.Search with
            {
                UseSqlFullText = true
            },
            new SearchInfrastructureCapabilities(
                SupportsSqlFullText: true,
                SupportsHybridSearch: false));
        Expect.True(sqlFullTextCapabilities.UseSqlFullText, "Effective search capabilities should allow SQL Full-Text when it is explicitly enabled and the runtime can provide it.", failures);
        Expect.Equal(SearchBackend.SqlFullText, selector.Select(sqlFullTextCapabilities), "Search backend selector should still prefer SQL Full-Text when it is explicitly enabled and available.", failures);

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

        var productionCapabilities = SystemCapabilities.FromOptions(
            RuntimeProfile.Production,
            new CapabilityOptions
            {
                Ai = true,
                Ocr = true,
                Replay = true,
                SemanticSearch = true,
                HybridSearch = true
        });
        Expect.Equal(SearchBackend.HybridVector, selector.Select(productionCapabilities.Search), "Search backend selector should use hybrid/vector mode when the production profile enables hybrid search.", failures);

        var degradedProductionCapabilities = SearchCapabilitiesRuntimeResolver.Resolve(
            productionCapabilities.Search,
            new SearchInfrastructureCapabilities(
                SupportsSqlFullText: true,
                SupportsHybridSearch: false));
        Expect.False(degradedProductionCapabilities.UseHybridSearch, "Effective search capabilities should disable hybrid search when OpenSearch runtime support is unavailable.", failures);
        Expect.False(degradedProductionCapabilities.UseSemanticSearch, "Effective search capabilities should disable semantic search when OpenSearch runtime support is unavailable.", failures);
        Expect.Equal(SearchBackend.ProjectionIndex, selector.Select(degradedProductionCapabilities), "Search backend selector should fall back to the projection index when hybrid search is configured but unavailable.", failures);

        var truthfulCapabilitiesProvider = new ConfigurationSystemCapabilitiesProvider(
            new StaticOptionsMonitor<LawWatcherRuntimeOptions>(new LawWatcherRuntimeOptions
            {
                Profile = RuntimeProfile.Production.Value,
                Capabilities = new CapabilityOptions
                {
                    Ai = true,
                    Ocr = true,
                    Replay = true,
                    SemanticSearch = true,
                    HybridSearch = true
                }
            }),
            new SearchInfrastructureCapabilities(
                SupportsSqlFullText: true,
                SupportsHybridSearch: true),
            new AiInfrastructureCapabilities(SupportsConfiguredLocalLlm: true),
            new OcrInfrastructureCapabilities(SupportsConfiguredDocumentPipeline: false));
        Expect.False(truthfulCapabilitiesProvider.Current.OcrEnabled, "System capabilities provider should keep OCR disabled when the configured document pipeline is not actually available.", failures);

        var enabledTruthfulCapabilitiesProvider = new ConfigurationSystemCapabilitiesProvider(
            new StaticOptionsMonitor<LawWatcherRuntimeOptions>(new LawWatcherRuntimeOptions
            {
                Profile = RuntimeProfile.Production.Value,
                Capabilities = new CapabilityOptions
                {
                    Ai = true,
                    Ocr = true,
                    Replay = true,
                    SemanticSearch = true,
                    HybridSearch = true
                }
            }),
            new SearchInfrastructureCapabilities(
                SupportsSqlFullText: true,
                SupportsHybridSearch: true),
            new AiInfrastructureCapabilities(SupportsConfiguredLocalLlm: true),
            new OcrInfrastructureCapabilities(SupportsConfiguredDocumentPipeline: true));
        Expect.True(enabledTruthfulCapabilitiesProvider.Current.OcrEnabled, "System capabilities provider should expose OCR only when the configured document pipeline is available.", failures);

        var llmPolicy = LocalLlmExecutionPolicy.For(RuntimeProfile.Dev);
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
                            new InboxConsumerDiagnosticsSnapshot("worker-lite.replay-requested", 2, new DateTimeOffset(2026, 03, 27, 10, 5, 0, 0, TimeSpan.Zero)),
                            new InboxConsumerDiagnosticsSnapshot("worker-lite.backfill-requested", 1, new DateTimeOffset(2026, 03, 27, 10, 7, 0, 0, TimeSpan.Zero))
                        ]))),
            new StubBrokerDiagnosticsStore(
                new BrokerDiagnosticsSnapshot(
                    true,
                    4,
                    2,
                    3,
                    2,
                    1,
                    1,
                    0,
                    2,
                    [
                        new BrokerEndpointDiagnosticsSnapshot(
                            "ai-enrichment-requested",
                            "ai-enrichment-requested",
                            "running",
                            1,
                            2,
                            1,
                            1,
                            0,
                            0,
                            1),
                        new BrokerEndpointDiagnosticsSnapshot(
                            "replay-requested",
                            "replay-requested",
                            "running",
                            1,
                            1,
                            1,
                            0,
                            1,
                            0,
                            1)
                    ])),
            sqlOutboxEnabled: true,
            brokerEnabled: true);
        var messagingDiagnostics = await messagingDiagnosticsQueryService.GetDiagnosticsAsync(CancellationToken.None);

        Expect.Equal("rabbitmq", messagingDiagnostics.DeliveryMode, "Messaging diagnostics query service should report RabbitMQ as the primary delivery mode when broker transport is configured.", failures);
        Expect.Equal("fallback", messagingDiagnostics.PollerMode, "Messaging diagnostics query service should describe SQL polling as fallback-only in broker mode.", failures);
        Expect.True(messagingDiagnostics.DiagnosticsAvailable, "Messaging diagnostics query service should preserve store availability in the response contract.", failures);
        Expect.Equal(1, messagingDiagnostics.Outbox.DeferredCount, "Messaging diagnostics query service should map deferred outbox counts into the response contract.", failures);
        Expect.Equal(2, messagingDiagnostics.Outbox.MessageTypes.Count, "Messaging diagnostics query service should expose grouped per-message-type outbox diagnostics.", failures);
        Expect.Equal("worker-lite.replay-requested", messagingDiagnostics.Inbox.Consumers.First().ConsumerName, "Messaging diagnostics query service should preserve inbox consumer names in the response contract.", failures);
        Expect.True(messagingDiagnostics.Broker.DiagnosticsAvailable, "Messaging diagnostics query service should expose broker telemetry availability when the broker diagnostics adapter is configured.", failures);
        Expect.Equal(1, messagingDiagnostics.Broker.FaultCount, "Messaging diagnostics query service should expose broker fault queue counts in the response contract.", failures);
        Expect.Equal(2L, messagingDiagnostics.Broker.RedeliveryCount, "Messaging diagnostics query service should aggregate broker redelivery counts in the response contract.", failures);
        Expect.Equal("ai-enrichment-requested", messagingDiagnostics.Broker.Endpoints.First().EndpointName, "Messaging diagnostics query service should preserve broker endpoint names in the response contract.", failures);
        Expect.Equal("running", messagingDiagnostics.Broker.Endpoints.First().Status, "Messaging diagnostics query service should preserve broker consumer status in the response contract.", failures);

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
                    "search_documents older than the requested retention window were pruned by indexed_at_utc.",
                    new DateTimeOffset(2026, 03, 06, 11, 0, 0, TimeSpan.Zero),
                    4,
                    true,
                    "completed and failed ai_enrichment_tasks older than the requested retention window were pruned by terminal timestamps.",
                    new DateTimeOffset(2026, 03, 01, 11, 0, 0, TimeSpan.Zero),
                    3,
                    true,
                    "derived OCR and AI document artifacts older than the requested retention window were pruned by created_at_utc.")));
        var retentionMaintenance = await retentionMaintenanceCommandService.RunAsync(
            new RunRetentionMaintenanceCommand(
                168,
                336,
                720,
                672,
                504,
                336)
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
        Expect.Equal(new DateTimeOffset(2026, 03, 06, 11, 0, 0, TimeSpan.Zero), retentionMaintenance.AiTasksCutoffUtc ?? DateTimeOffset.MinValue, "Retention maintenance command service should map the AI-task retention cutoff into the response contract.", failures);
        Expect.Equal(4, retentionMaintenance.DeletedAiTasksCount, "Retention maintenance command service should map deleted AI-task rows into the response contract.", failures);
        Expect.True(retentionMaintenance.AiTasksRetentionApplied, "Retention maintenance command service should report when AI-task retention was applied.", failures);
        Expect.Equal("completed and failed ai_enrichment_tasks older than the requested retention window were pruned by terminal timestamps.", retentionMaintenance.AiTasksRetentionReason, "Retention maintenance command service should preserve the explicit AI-task retention reason.", failures);
        Expect.Equal(new DateTimeOffset(2026, 03, 01, 11, 0, 0, TimeSpan.Zero), retentionMaintenance.DocumentArtifactsCutoffUtc ?? DateTimeOffset.MinValue, "Retention maintenance command service should map the requested document-artifact retention cutoff into the response contract.", failures);
        Expect.Equal(3, retentionMaintenance.DeletedDocumentArtifactsCount, "Retention maintenance command service should preserve deleted document-artifact counts in the response contract.", failures);
        Expect.True(retentionMaintenance.DocumentArtifactsRetentionApplied, "Retention maintenance command service should report when document-artifact retention was applied.", failures);
        Expect.Equal("derived OCR and AI document artifacts older than the requested retention window were pruned by created_at_utc.", retentionMaintenance.DocumentArtifactsRetentionReason, "Retention maintenance command service should preserve the explicit document-artifact retention reason.", failures);

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

        Expect.Equal(expectedStateRoot, resolvedStateRoot, "State storage path resolver should normalize the shared root relative to the host content root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "ai-enrichment", "tasks"), statePaths.AiTasksRoot, "State storage paths should derive a stable AI task root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "integration-api", "replays"), statePaths.ReplaysRoot, "State storage paths should derive a stable replay root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "integration-api", "backfills"), statePaths.BackfillsRoot, "State storage paths should derive a stable backfill root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "taxonomy-and-profiles", "subscriptions"), statePaths.ProfileSubscriptionsRoot, "State storage paths should derive a stable subscription root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "integration-api", "webhook-registrations"), statePaths.WebhookRegistrationsRoot, "State storage paths should derive a stable webhook registration root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "notifications", "bill-alerts"), statePaths.BillAlertsRoot, "State storage paths should derive a stable bill alert root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "notifications", "dispatches"), statePaths.NotificationDispatchesRoot, "State storage paths should derive a stable notification dispatch root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "integration-api", "webhook-dispatches"), statePaths.WebhookEventDispatchesRoot, "State storage paths should derive a stable webhook event dispatch root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "taxonomy-and-profiles", "monitoring-profiles"), statePaths.MonitoringProfilesRoot, "State storage paths should derive a stable monitoring profile root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "legislative-intake", "bills"), statePaths.BillsRoot, "State storage paths should derive a stable imported bill root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "legislative-process", "processes"), statePaths.ProcessesRoot, "State storage paths should derive a stable legislative process root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "legal-corpus", "acts"), statePaths.ActsRoot, "State storage paths should derive a stable published act root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "identity-and-access", "api-clients"), statePaths.ApiClientsRoot, "State storage paths should derive a stable API client root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "identity-and-access", "operator-accounts"), statePaths.OperatorAccountsRoot, "State storage paths should derive a stable operator account root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "search", "documents"), statePaths.SearchIndexRoot, "State storage paths should derive a stable search index root from the shared state root.", failures);
        Expect.Equal(Path.Combine(expectedStateRoot, "integration-api", "events"), statePaths.EventFeedRoot, "State storage paths should derive a stable event feed root from the shared state root.", failures);

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
        Expect.Equal(DocumentStoreBackend.S3Compatible, DocumentStoreRuntimeResolver.Select(objectStorageOptions), "Document store runtime resolver should choose the S3-compatible backend when MinIO credentials are configured.", failures);
        Expect.Equal(
            Path.GetFullPath(Path.Combine(expectedRepositoryRoot, "src", "artifacts", "documents")),
            DocumentStoreRuntimeResolver.ResolveLocalDocumentsRoot(objectStorageOptions, Path.Combine(expectedRepositoryRoot, "src", "Server", "LawWatcher.Api")),
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
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var readmePath = Path.Combine(current.FullName, "README.md");
            var solutionPath = Path.Combine(current.FullName, "LawWatcher.slnx");
            if (File.Exists(readmePath) && File.Exists(solutionPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root for runtime contract checks.");
    }

    private static bool ThrowsUnsupportedRuntimeProfile(string value)
    {
        try
        {
            RuntimeProfile.Parse(value);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    private static void AssertFrozenSupportedSurfaceUsesCanonicalNames(string repoRoot, List<string> failures)
    {
        var docsAndContracts =
            new[]
            {
                "README.md",
                "docs/INSTALL.md",
                "docs/CONFIGURATION.md",
                "docs/ARCHITECTURE.md",
                "docs/VERIFICATION.md",
                "docs/RUNBOOK.md",
                ".github/workflows/docker-smoke.yml"
            };

        var opsFiles = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "ops"), "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}sql{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path =>
                path.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".example", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'));

        var appSettingsFiles = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src"), "appsettings.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.artifacts{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'));

        var supportedFiles = docsAndContracts
            .Concat(opsFiles)
            .Concat(appSettingsFiles)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var forbiddenMarkers =
            new[]
            {
                "full-host",
                "dev-laptop",
                "search:read",
                "alerts:read"
            };

        foreach (var relativePath in supportedFiles)
        {
            var absolutePath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var content = File.ReadAllText(absolutePath);

            foreach (var marker in forbiddenMarkers)
            {
                Expect.False(
                    content.Contains(marker, StringComparison.Ordinal),
                    $"{relativePath} should not contain legacy marker '{marker}'.",
                    failures);
            }
        }
    }
}
