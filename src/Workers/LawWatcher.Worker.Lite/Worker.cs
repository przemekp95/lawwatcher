using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.Notifications.Application;
using Microsoft.Extensions.Options;

namespace LawWatcher.Worker.Lite;

public sealed class Worker(
    ILogger<Worker> logger,
    IConfiguration configuration,
    IOptionsMonitor<LawWatcherRuntimeOptions> runtimeOptions,
    IOptionsMonitor<StateStorageOptions> stateStorageOptions,
    IOptionsMonitor<WorkerLiteOptions> workerLiteOptions,
    MonitoringProfileProjectionOutboxProcessor monitoringProfileProjectionOutboxProcessor,
    BillProjectionOutboxProcessor billProjectionOutboxProcessor,
    ProcessProjectionOutboxProcessor processProjectionOutboxProcessor,
    ActProjectionOutboxProcessor actProjectionOutboxProcessor,
    ProfileSubscriptionNotificationOutboxProcessor profileSubscriptionNotificationOutboxProcessor,
    WebhookRegistrationDispatchOutboxProcessor webhookRegistrationDispatchOutboxProcessor,
    AlertProjectionRefreshService alertProjectionRefreshService,
    EventFeedProjectionRefreshService eventFeedProjectionRefreshService,
    SearchProjectionRefreshService searchProjectionRefreshService,
    ReplayQueueProcessor replayQueueProcessor,
    BackfillQueueProcessor backfillQueueProcessor,
    AlertCreatedOutboxProcessor alertCreatedOutboxProcessor,
    AlertNotificationDispatchService alertNotificationDispatchService,
    AlertWebhookDispatchService alertWebhookDispatchService) : BackgroundService
{
    private static readonly TimeSpan BrokerProjectionCatchupPassDelay = TimeSpan.FromSeconds(5);
    private const int BrokerProjectionCatchupPassLimit = 6;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var brokerModeLogged = false;
        var brokerProjectionCatchupCompleted = false;
        var brokerProjectionCatchupPassesCompleted = 0;
        DateTimeOffset? nextBrokerProjectionCatchupAtUtc = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var runtimeProfile = RuntimeProfile.Parse(runtimeOptions.CurrentValue.Profile);
            var capabilities = SystemCapabilities.FromOptions(runtimeProfile, runtimeOptions.CurrentValue.Capabilities);
            var enabledPipelines = new HashSet<string>(
                WorkerLitePipelineConfiguration.ResolveEnabledPipelines(configuration),
                StringComparer.OrdinalIgnoreCase);
            var rabbitMqBrokerModeEnabled =
                !string.IsNullOrWhiteSpace(configuration.GetConnectionString("RabbitMq")) &&
                string.Equals(stateStorageOptions.CurrentValue.Provider, "sqlserver", StringComparison.OrdinalIgnoreCase);

            var notificationsEnabled = enabledPipelines.Contains("notifications");
            var projectionEnabled = enabledPipelines.Contains("projection");
            var replayOrBackfillEnabled = capabilities.ReplayEnabled && (enabledPipelines.Contains("replay") || enabledPipelines.Contains("backfill"));

            if (!notificationsEnabled && !projectionEnabled && !replayOrBackfillEnabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                continue;
            }

            if ((replayOrBackfillEnabled || notificationsEnabled || projectionEnabled) && rabbitMqBrokerModeEnabled && !brokerModeLogged)
            {
                logger.LogInformation(
                    "worker-lite broker consumer mode enabled for replay/backfill, monitoring profile projection, bill projection, process projection, act projection, bill-alert dispatch and admin catch-up flows. profile={Profile}",
                    runtimeProfile.Value);
                brokerModeLogged = true;
            }

            if (!rabbitMqBrokerModeEnabled)
            {
                brokerProjectionCatchupCompleted = false;
                brokerProjectionCatchupPassesCompleted = 0;
                nextBrokerProjectionCatchupAtUtc = null;
            }

            if (projectionEnabled && rabbitMqBrokerModeEnabled && !brokerProjectionCatchupCompleted)
            {
                if (nextBrokerProjectionCatchupAtUtc is { } nextCatchupAtUtc &&
                    nextCatchupAtUtc > DateTimeOffset.UtcNow)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                var initialAlertRefresh = await alertProjectionRefreshService.RefreshAsync(stoppingToken);
                var initialEventFeedRefresh = await eventFeedProjectionRefreshService.RefreshAsync(stoppingToken);
                var initialSearchRefresh = await searchProjectionRefreshService.RefreshAsync(stoppingToken);
                brokerProjectionCatchupPassesCompleted++;

                logger.LogInformation(
                    "worker-lite broker projection catch-up pass completed. profile={Profile} pass={Pass} passLimit={PassLimit} alertsGenerated={AlertCount} eventFeedRebuilt={EventFeedRebuilt} eventFeedItems={EventFeedCount} searchRebuilt={SearchRebuilt} searchDocuments={SearchDocumentCount}",
                    runtimeProfile.Value,
                    brokerProjectionCatchupPassesCompleted,
                    BrokerProjectionCatchupPassLimit,
                    initialAlertRefresh.GeneratedCount,
                    initialEventFeedRefresh.HasRebuilt,
                    initialEventFeedRefresh.EventCount,
                    initialSearchRefresh.HasRebuilt,
                    initialSearchRefresh.DocumentCount);

                brokerProjectionCatchupCompleted = brokerProjectionCatchupPassesCompleted >= BrokerProjectionCatchupPassLimit;
                nextBrokerProjectionCatchupAtUtc = brokerProjectionCatchupCompleted
                    ? null
                    : DateTimeOffset.UtcNow.Add(BrokerProjectionCatchupPassDelay);

                if (initialAlertRefresh.GeneratedCount > 0 ||
                    initialEventFeedRefresh.HasRebuilt ||
                    initialSearchRefresh.HasRebuilt ||
                    !brokerProjectionCatchupCompleted)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }
            }

            var batchSize = Math.Max(1, workerLiteOptions.CurrentValue.MaxConcurrency);
            var monitoringProfileProjectionBatch = projectionEnabled && !rabbitMqBrokerModeEnabled
                ? await monitoringProfileProjectionOutboxProcessor.ProcessAvailableAsync(batchSize, stoppingToken)
                : new MonitoringProfileProjectionOutboxProcessingResult(0, false);
            var billProjectionBatch = projectionEnabled && !rabbitMqBrokerModeEnabled
                ? await billProjectionOutboxProcessor.ProcessAvailableAsync(batchSize, stoppingToken)
                : new BillProjectionOutboxProcessingResult(0, false);
            var processProjectionBatch = projectionEnabled && !rabbitMqBrokerModeEnabled
                ? await processProjectionOutboxProcessor.ProcessAvailableAsync(batchSize, stoppingToken)
                : new ProcessProjectionOutboxProcessingResult(0, false);
            var actProjectionBatch = projectionEnabled && !rabbitMqBrokerModeEnabled
                ? await actProjectionOutboxProcessor.ProcessAvailableAsync(batchSize, stoppingToken)
                : new ActProjectionOutboxProcessingResult(0, false);
            var profileSubscriptionNotificationBatch = notificationsEnabled && !rabbitMqBrokerModeEnabled
                ? await profileSubscriptionNotificationOutboxProcessor.ProcessAvailableAsync(batchSize, stoppingToken)
                : new ProfileSubscriptionNotificationOutboxProcessingResult(0, false);
            var webhookRegistrationDispatchBatch = notificationsEnabled && !rabbitMqBrokerModeEnabled
                ? await webhookRegistrationDispatchOutboxProcessor.ProcessAvailableAsync(batchSize, stoppingToken)
                : new WebhookRegistrationDispatchOutboxProcessingResult(0, false);
            var alertRefresh = projectionEnabled && !rabbitMqBrokerModeEnabled
                ? await alertProjectionRefreshService.RefreshAsync(stoppingToken)
                : new AlertProjectionRefreshResult(0);
            var searchRefresh = projectionEnabled && !rabbitMqBrokerModeEnabled
                ? await searchProjectionRefreshService.RefreshAsync(stoppingToken)
                : new SearchProjectionRefreshResult(0, false);
            var replayBatch = replayOrBackfillEnabled && enabledPipelines.Contains("replay") && !rabbitMqBrokerModeEnabled
                ? await replayQueueProcessor.ProcessAvailableAsync(batchSize, stoppingToken)
                : new ReplayBatchProcessingResult(0, false);
            var backfillBatch = replayOrBackfillEnabled && enabledPipelines.Contains("backfill") && !rabbitMqBrokerModeEnabled
                ? await backfillQueueProcessor.ProcessAvailableAsync(batchSize, stoppingToken)
                : new BackfillBatchProcessingResult(0, false);
            var alertCreatedBatch = notificationsEnabled && !rabbitMqBrokerModeEnabled
                ? await alertCreatedOutboxProcessor.ProcessAvailableAsync(batchSize, stoppingToken)
                : new AlertCreatedOutboxProcessingResult(0, false);
            var notificationBatch = notificationsEnabled && !rabbitMqBrokerModeEnabled
                ? await alertNotificationDispatchService.DispatchPendingAsync(stoppingToken)
                : new AlertNotificationDispatchResult(0, 0);
            var webhookBatch = notificationsEnabled && !rabbitMqBrokerModeEnabled
                ? await alertWebhookDispatchService.DispatchPendingAsync(stoppingToken)
                : new AlertWebhookDispatchResult(0);
            var eventFeedRefresh = projectionEnabled && !rabbitMqBrokerModeEnabled
                ? await eventFeedProjectionRefreshService.RefreshAsync(stoppingToken)
                : new EventFeedProjectionRefreshResult(0, false);

            if (monitoringProfileProjectionBatch.ProcessedCount > 0 || billProjectionBatch.ProcessedCount > 0 || processProjectionBatch.ProcessedCount > 0 || actProjectionBatch.ProcessedCount > 0 || profileSubscriptionNotificationBatch.ProcessedCount > 0 || webhookRegistrationDispatchBatch.ProcessedCount > 0 || alertRefresh.GeneratedCount > 0 || eventFeedRefresh.HasRebuilt || searchRefresh.HasRebuilt || replayBatch.ProcessedCount > 0 || backfillBatch.ProcessedCount > 0 || alertCreatedBatch.ProcessedCount > 0 || notificationBatch.ProcessedCount > 0 || webhookBatch.ProcessedCount > 0)
            {
                logger.LogInformation(
                    "worker-lite processed durable queues. profile={Profile} concurrency={Concurrency} monitoringProfileProjectionMessages={MonitoringProfileProjectionMessageCount} billProjectionMessages={BillProjectionMessageCount} processProjectionMessages={ProcessProjectionMessageCount} actProjectionMessages={ActProjectionMessageCount} profileSubscriptionNotificationMessages={ProfileSubscriptionNotificationMessageCount} webhookRegistrationDispatchMessages={WebhookRegistrationDispatchMessageCount} alertsGenerated={AlertCount} eventFeedRebuilt={EventFeedRebuilt} eventFeedItems={EventFeedCount} searchRebuilt={SearchRebuilt} searchDocuments={SearchDocumentCount} replays={ReplayCount} backfills={BackfillCount} alertCreatedMessages={AlertCreatedMessageCount} notifications={NotificationCount} skippedDigest={SkippedDigestCount} webhookEvents={WebhookCount} remainingMonitoringProfileProjectionMessages={RemainingMonitoringProfileProjectionMessages} remainingBillProjectionMessages={RemainingBillProjectionMessages} remainingProcessProjectionMessages={RemainingProcessProjectionMessages} remainingActProjectionMessages={RemainingActProjectionMessages} remainingProfileSubscriptionNotificationMessages={RemainingProfileSubscriptionNotificationMessages} remainingWebhookRegistrationDispatchMessages={RemainingWebhookRegistrationDispatchMessages} remainingReplays={RemainingReplays} remainingBackfills={RemainingBackfills} remainingAlertCreatedMessages={RemainingAlertCreatedMessages}",
                    runtimeProfile.Value,
                    batchSize,
                    monitoringProfileProjectionBatch.ProcessedCount,
                    billProjectionBatch.ProcessedCount,
                    processProjectionBatch.ProcessedCount,
                    actProjectionBatch.ProcessedCount,
                    profileSubscriptionNotificationBatch.ProcessedCount,
                    webhookRegistrationDispatchBatch.ProcessedCount,
                    alertRefresh.GeneratedCount,
                    eventFeedRefresh.HasRebuilt,
                    eventFeedRefresh.EventCount,
                    searchRefresh.HasRebuilt,
                    searchRefresh.DocumentCount,
                    replayBatch.ProcessedCount,
                    backfillBatch.ProcessedCount,
                    alertCreatedBatch.ProcessedCount,
                    notificationBatch.ProcessedCount,
                    notificationBatch.SkippedDigestCount,
                    webhookBatch.ProcessedCount,
                    monitoringProfileProjectionBatch.HasRemainingMessages,
                    billProjectionBatch.HasRemainingMessages,
                    processProjectionBatch.HasRemainingMessages,
                    actProjectionBatch.HasRemainingMessages,
                    profileSubscriptionNotificationBatch.HasRemainingMessages,
                    webhookRegistrationDispatchBatch.HasRemainingMessages,
                    replayBatch.HasRemainingQueuedRequests,
                    backfillBatch.HasRemainingQueuedRequests,
                    alertCreatedBatch.HasRemainingMessages);

                if (monitoringProfileProjectionBatch.HasRemainingMessages || billProjectionBatch.HasRemainingMessages || processProjectionBatch.HasRemainingMessages || actProjectionBatch.HasRemainingMessages || profileSubscriptionNotificationBatch.HasRemainingMessages || webhookRegistrationDispatchBatch.HasRemainingMessages || replayBatch.HasRemainingQueuedRequests || backfillBatch.HasRemainingQueuedRequests || alertCreatedBatch.HasRemainingMessages)
                {
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
