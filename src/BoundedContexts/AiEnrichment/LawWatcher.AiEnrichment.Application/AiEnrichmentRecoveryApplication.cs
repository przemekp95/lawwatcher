using LawWatcher.AiEnrichment.Contracts;
using LawWatcher.AiEnrichment.Domain.Tasks;
using LawWatcher.BuildingBlocks.Messaging;

namespace LawWatcher.AiEnrichment.Application;

public sealed record DocumentTextRecoveryResult(
    int MatchingQueuedTaskCount,
    int ProcessedTaskCount,
    int CompletedTaskCount,
    bool HasRemainingQueuedTasks);

public sealed class DocumentTextRecoveryService(
    IAiEnrichmentTaskReadRepository readRepository,
    AiEnrichmentExecutionService executionService)
{
    public async Task<DocumentTextRecoveryResult> RecoverAsync(
        DocumentTextExtractedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var queuedTasks = (await readRepository.GetTasksAsync(cancellationToken))
            .Where(task =>
                string.Equals(task.Status, "queued", StringComparison.OrdinalIgnoreCase)
                && string.Equals(task.SubjectType, integrationEvent.OwnerType, StringComparison.OrdinalIgnoreCase)
                && task.SubjectId == integrationEvent.OwnerId)
            .OrderBy(task => task.RequestedAtUtc)
            .ToArray();

        var processedTaskCount = 0;
        var completedTaskCount = 0;
        foreach (var queuedTask in queuedTasks)
        {
            var result = await executionService.ProcessAsync(new AiEnrichmentTaskId(queuedTask.Id), cancellationToken);
            if (!result.HasProcessedTask)
            {
                continue;
            }

            processedTaskCount += 1;
            if (string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                completedTaskCount += 1;
            }
        }

        var hasRemainingQueuedTasks = (await readRepository.GetTasksAsync(cancellationToken))
            .Any(task =>
                string.Equals(task.Status, "queued", StringComparison.OrdinalIgnoreCase)
                && string.Equals(task.SubjectType, integrationEvent.OwnerType, StringComparison.OrdinalIgnoreCase)
                && task.SubjectId == integrationEvent.OwnerId);

        return new DocumentTextRecoveryResult(
            queuedTasks.Length,
            processedTaskCount,
            completedTaskCount,
            hasRemainingQueuedTasks);
    }
}

public sealed class DocumentTextRecoveryMessageHandler(
    DocumentTextRecoveryService recoveryService,
    IInboxStore inboxStore)
{
    public const string ConsumerName = "worker-ai.document-text-recovery";

    public async Task<DocumentTextRecoveryResult> HandleAsync(
        DocumentTextExtractedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        if (await inboxStore.HasProcessedAsync(integrationEvent.EventId, ConsumerName, cancellationToken))
        {
            return new DocumentTextRecoveryResult(
                MatchingQueuedTaskCount: 0,
                ProcessedTaskCount: 0,
                CompletedTaskCount: 0,
                HasRemainingQueuedTasks: false);
        }

        var result = await recoveryService.RecoverAsync(integrationEvent, cancellationToken);
        await inboxStore.MarkProcessedAsync(integrationEvent.EventId, ConsumerName, cancellationToken);
        return result;
    }
}
