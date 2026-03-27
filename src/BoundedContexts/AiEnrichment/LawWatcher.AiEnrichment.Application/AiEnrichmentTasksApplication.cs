using LawWatcher.AiEnrichment.Contracts;
using LawWatcher.AiEnrichment.Domain.Tasks;
using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using System.Text.Json;
using LawWatcher.BuildingBlocks.Ports;

namespace LawWatcher.AiEnrichment.Application;

public sealed record RequestAiEnrichmentCommand(
    Guid TaskId,
    string Kind,
    string SubjectType,
    Guid SubjectId,
    string SubjectTitle,
    string Prompt) : Command;

public sealed record AiEnrichmentTaskReadModel(
    Guid Id,
    string Kind,
    string SubjectType,
    Guid SubjectId,
    string SubjectTitle,
    string Status,
    string? Model,
    string? Content,
    string? Error,
    IReadOnlyCollection<string> Citations,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? FailedAtUtc);

public interface IAiEnrichmentTaskRepository
{
    Task<AiEnrichmentTask?> GetAsync(AiEnrichmentTaskId id, CancellationToken cancellationToken);

    Task<AiEnrichmentTask?> GetNextQueuedAsync(CancellationToken cancellationToken);

    Task SaveAsync(AiEnrichmentTask task, CancellationToken cancellationToken);
}

public interface IAiEnrichmentTaskOutboxWriter
{
    Task SaveAsync(
        AiEnrichmentTask task,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken);
}

public interface IAiEnrichmentTaskReadRepository
{
    Task<IReadOnlyCollection<AiEnrichmentTaskReadModel>> GetTasksAsync(CancellationToken cancellationToken);
}

public interface IAiEnrichmentTaskProjection
{
    Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}

public sealed class AiEnrichmentCommandService(
    IAiEnrichmentTaskRepository repository,
    IAiEnrichmentTaskProjection projection)
{
    public async Task RequestAsync(RequestAiEnrichmentCommand command, CancellationToken cancellationToken)
    {
        var taskId = new AiEnrichmentTaskId(command.TaskId);
        var existing = await repository.GetAsync(taskId, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"AI enrichment task '{command.TaskId}' has already been created.");
        }

        var task = AiEnrichmentTask.Request(
            taskId,
            AiTaskKind.Of(command.Kind),
            AiTaskSubject.Create(command.SubjectType, command.SubjectId, command.SubjectTitle),
            command.Prompt,
            command.RequestedAtUtc);

        await SaveAndProjectAsync(
            task,
            [
                new AiEnrichmentRequestedIntegrationEvent(
                    command.CommandId,
                    command.RequestedAtUtc,
                    command.TaskId,
                    command.Kind,
                    command.SubjectType,
                    command.SubjectId,
                    command.SubjectTitle)
            ],
            cancellationToken);
    }

    private async Task SaveAndProjectAsync(
        AiEnrichmentTask task,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        var pendingEvents = task.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        if (repository is IAiEnrichmentTaskOutboxWriter outboxWriter && integrationEvents.Count != 0)
        {
            await outboxWriter.SaveAsync(task, integrationEvents, cancellationToken);
        }
        else
        {
            await repository.SaveAsync(task, cancellationToken);
        }

        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }
}

public sealed record AiEnrichmentExecutionResult(
    bool HasProcessedTask,
    Guid? TaskId,
    string? Status);

public sealed record AiEnrichmentBatchProcessingResult(
    int ProcessedCount,
    bool HasRemainingQueuedTasks);

public sealed record AiPromptAugmentation(
    string Prompt,
    IReadOnlyCollection<string> Citations);

public interface IAiPromptAugmentor
{
    Task<AiPromptAugmentation> AugmentAsync(
        AiTaskSubject subject,
        string prompt,
        CancellationToken cancellationToken);
}

public sealed class PassthroughAiPromptAugmentor : IAiPromptAugmentor
{
    public Task<AiPromptAugmentation> AugmentAsync(
        AiTaskSubject subject,
        string prompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new AiPromptAugmentation(prompt, []));
    }
}

public sealed class AiEnrichmentExecutionService(
    IAiEnrichmentTaskRepository repository,
    IAiEnrichmentTaskProjection projection,
    ILlmService llmService,
    IAiPromptAugmentor promptAugmentor)
{
    public async Task<AiEnrichmentExecutionResult> ProcessNextQueuedAsync(CancellationToken cancellationToken)
    {
        var task = await repository.GetNextQueuedAsync(cancellationToken);
        if (task is null)
        {
            return new AiEnrichmentExecutionResult(false, null, null);
        }

        return await ProcessAsync(task.Id, cancellationToken);
    }

    public async Task<AiEnrichmentExecutionResult> ProcessAsync(AiEnrichmentTaskId taskId, CancellationToken cancellationToken)
    {
        var task = await repository.GetAsync(taskId, cancellationToken);
        if (task is null)
        {
            return new AiEnrichmentExecutionResult(false, taskId.Value, null);
        }

        if (!string.Equals(task.Status.Code, "queued", StringComparison.OrdinalIgnoreCase))
        {
            return new AiEnrichmentExecutionResult(false, task.Id.Value, task.Status.Code);
        }

        AiPromptAugmentation promptAugmentation;
        try
        {
            promptAugmentation = await promptAugmentor.AugmentAsync(task.Subject, task.Prompt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DerivedDocumentTextNotReadyException)
        {
            throw;
        }

        task.MarkStarted(DateTimeOffset.UtcNow);
        await SaveAndProjectAsync(task, cancellationToken);

        try
        {
            var completion = await llmService.CompleteAsync(promptAugmentation.Prompt, cancellationToken);
            task.MarkCompleted(
                completion.Model,
                completion.Content,
                MergeCitations(promptAugmentation.Citations, completion.Citations),
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            task.MarkFailed(ex.Message, DateTimeOffset.UtcNow);
        }

        await SaveAndProjectAsync(task, cancellationToken);
        return new AiEnrichmentExecutionResult(true, task.Id.Value, task.Status.Code);
    }

    private async Task SaveAndProjectAsync(AiEnrichmentTask task, CancellationToken cancellationToken)
    {
        var pendingEvents = task.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        await repository.SaveAsync(task, cancellationToken);
        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }

    private static IReadOnlyCollection<string> MergeCitations(
        IReadOnlyCollection<string> augmentorCitations,
        IReadOnlyCollection<string> completionCitations)
    {
        return augmentorCitations
            .Concat(completionCitations)
            .Select(citation => citation.Trim())
            .Where(citation => citation.Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed class AiEnrichmentQueueProcessor(
    IAiEnrichmentTaskRepository repository,
    AiEnrichmentExecutionService executionService,
    IOutboxMessageStore? outboxMessageStore = null,
    IInboxStore? inboxStore = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] RequestedMessageTypes = [typeof(AiEnrichmentRequestedIntegrationEvent).FullName ?? nameof(AiEnrichmentRequestedIntegrationEvent)];
    public const string ConsumerName = "worker-ai.ai-enrichment-requested";

    public async Task<AiEnrichmentBatchProcessingResult> ProcessAvailableAsync(int maxTasks, CancellationToken cancellationToken)
    {
        if (maxTasks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTasks), maxTasks, "Maximum batch size must be greater than zero.");
        }

        if (outboxMessageStore?.SupportsPolling == true && inboxStore is not null)
        {
            return await ProcessFromOutboxAsync(maxTasks, cancellationToken);
        }

        var processedCount = 0;
        for (var processed = 0; processed < maxTasks; processed++)
        {
            var executionResult = await executionService.ProcessNextQueuedAsync(cancellationToken);
            if (!executionResult.HasProcessedTask)
            {
                return new AiEnrichmentBatchProcessingResult(processedCount, false);
            }

            processedCount++;
        }

        var hasRemainingQueuedTasks = await repository.GetNextQueuedAsync(cancellationToken) is not null;
        return new AiEnrichmentBatchProcessingResult(processedCount, hasRemainingQueuedTasks);
    }

    private async Task<AiEnrichmentBatchProcessingResult> ProcessFromOutboxAsync(int maxTasks, CancellationToken cancellationToken)
    {
        var messages = await outboxMessageStore!.GetPendingAsync(RequestedMessageTypes, maxTasks, cancellationToken);
        var processedCount = 0;

        foreach (var message in messages)
        {
            if (await inboxStore!.HasProcessedAsync(message.MessageId, ConsumerName, cancellationToken))
            {
                await outboxMessageStore.MarkPublishedAsync(message.MessageId, DateTimeOffset.UtcNow, cancellationToken);
                processedCount++;
                continue;
            }

            try
            {
                var integrationEvent = JsonSerializer.Deserialize<AiEnrichmentRequestedIntegrationEvent>(message.Payload, SerializerOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(AiEnrichmentRequestedIntegrationEvent)}'.");

                await executionService.ProcessAsync(new AiEnrichmentTaskId(integrationEvent.TaskId), cancellationToken);
                await inboxStore.MarkProcessedAsync(message.MessageId, ConsumerName, cancellationToken);
                await outboxMessageStore.MarkPublishedAsync(message.MessageId, DateTimeOffset.UtcNow, cancellationToken);
                processedCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await outboxMessageStore.DeferAsync(message.MessageId, DateTimeOffset.UtcNow.AddSeconds(15), cancellationToken);
            }
        }

        var hasRemainingQueuedTasks = (await outboxMessageStore.GetPendingAsync(RequestedMessageTypes, 1, cancellationToken)).Count != 0;
        return new AiEnrichmentBatchProcessingResult(processedCount, hasRemainingQueuedTasks);
    }
}

public sealed class AiEnrichmentTasksQueryService(IAiEnrichmentTaskReadRepository repository)
{
    public async Task<IReadOnlyList<AiEnrichmentTaskResponse>> GetTasksAsync(CancellationToken cancellationToken)
    {
        var tasks = await repository.GetTasksAsync(cancellationToken);

        return tasks
            .OrderByDescending(task => task.RequestedAtUtc)
            .ThenBy(task => task.SubjectTitle, StringComparer.OrdinalIgnoreCase)
            .Select(task => new AiEnrichmentTaskResponse(
                task.Id,
                task.Kind,
                task.SubjectType,
                task.SubjectId,
                task.SubjectTitle,
                task.Status,
                task.Model,
                task.Content,
                task.Error,
                task.Citations.ToArray(),
                task.RequestedAtUtc,
                task.StartedAtUtc,
                task.CompletedAtUtc,
                task.FailedAtUtc))
            .ToArray();
    }
}
