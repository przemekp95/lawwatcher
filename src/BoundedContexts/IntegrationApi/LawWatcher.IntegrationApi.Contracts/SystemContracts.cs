using LawWatcher.SearchAndDiscovery.Contracts;

namespace LawWatcher.IntegrationApi.Contracts;

public sealed record AiCapabilityResponse(
    bool Enabled,
    string ActivationMode,
    int MaxConcurrency,
    int UnloadAfterIdleSeconds);

public sealed record SearchCapabilityResponse(
    bool UseSqlFullText,
    bool UseHybridSearch,
    bool UseSemanticSearch,
    SearchBackend Backend);

public sealed record SystemCapabilitiesResponse(
    string RuntimeProfile,
    AiCapabilityResponse Ai,
    SearchCapabilityResponse Search,
    bool OcrEnabled,
    bool ReplayEnabled);

public sealed record MessagingOutboxMessageTypeResponse(
    string MessageType,
    int TotalCount,
    int PendingCount,
    int ReadyCount,
    int DeferredCount,
    int PublishedCount,
    int MaxAttemptCount);

public sealed record MessagingOutboxResponse(
    int TotalCount,
    int PendingCount,
    int ReadyCount,
    int DeferredCount,
    int PublishedCount,
    int MaxAttemptCount,
    DateTimeOffset? OldestPendingCreatedAtUtc,
    DateTimeOffset? NextScheduledAttemptAtUtc,
    IReadOnlyCollection<MessagingOutboxMessageTypeResponse> MessageTypes);

public sealed record MessagingInboxConsumerResponse(
    string ConsumerName,
    int ProcessedCount,
    DateTimeOffset? LastProcessedAtUtc);

public sealed record MessagingInboxResponse(
    int ProcessedCount,
    IReadOnlyCollection<MessagingInboxConsumerResponse> Consumers);

public sealed record MessagingDiagnosticsResponse(
    string DeliveryMode,
    string PollerMode,
    bool BrokerEnabled,
    bool SqlOutboxEnabled,
    bool DiagnosticsAvailable,
    MessagingOutboxResponse Outbox,
    MessagingInboxResponse Inbox);

public sealed record RunRetentionMaintenanceRequest(
    int PublishedOutboxRetentionHours,
    int ProcessedInboxRetentionHours,
    int EventFeedRetentionHours,
    int? SearchDocumentsRetentionHours);

public sealed record RetentionMaintenanceResponse(
    bool MaintenanceAvailable,
    DateTimeOffset ExecutedAtUtc,
    DateTimeOffset PublishedOutboxCutoffUtc,
    DateTimeOffset ProcessedInboxCutoffUtc,
    DateTimeOffset EventFeedCutoffUtc,
    int DeletedPublishedOutboxCount,
    int DeletedProcessedInboxCount,
    int DeletedEventFeedCount,
    DateTimeOffset? SearchDocumentsCutoffUtc,
    int DeletedSearchDocumentsCount,
    bool SearchDocumentsRetentionApplied,
    string SearchDocumentsRetentionReason);

public sealed record SearchHitResponse(
    string Id,
    string Title,
    string Type,
    string Snippet);

public sealed record SearchQueryResponse(
    string Query,
    SearchBackend Backend,
    IReadOnlyCollection<SearchHitResponse> Hits);
