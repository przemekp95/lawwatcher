using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Contracts;
using LawWatcher.IntegrationApi.Domain.Backfills;
using System.Text.Json;

namespace LawWatcher.IntegrationApi.Application;

public sealed record RequestBackfillCommand(
    Guid BackfillRequestId,
    BackfillSource Source,
    BackfillScope Scope,
    DateOnly RequestedFrom,
    DateOnly? RequestedTo,
    string RequestedBy) : Command;

public sealed record MarkBackfillStartedCommand(
    Guid BackfillRequestId) : Command;

public sealed record MarkBackfillCompletedCommand(
    Guid BackfillRequestId) : Command;

public sealed record BackfillRequestReadModel(
    Guid Id,
    string Source,
    string Scope,
    string Status,
    string RequestedBy,
    DateOnly RequestedFrom,
    DateOnly? RequestedTo,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public interface IBackfillRequestRepository
{
    Task<BackfillRequest?> GetAsync(BackfillRequestId id, CancellationToken cancellationToken);

    Task<BackfillRequest?> GetNextQueuedAsync(CancellationToken cancellationToken);

    Task SaveAsync(BackfillRequest backfillRequest, CancellationToken cancellationToken);
}

public interface IBackfillRequestOutboxWriter
{
    Task SaveAsync(
        BackfillRequest backfillRequest,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken);
}

public interface IBackfillRequestReadRepository
{
    Task<IReadOnlyCollection<BackfillRequestReadModel>> GetBackfillsAsync(CancellationToken cancellationToken);
}

public interface IBackfillRequestProjection
{
    Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}

public sealed record BackfillExecutionResult(
    bool HasProcessedRequest,
    Guid? BackfillRequestId,
    string? Status);

public sealed record BackfillBatchProcessingResult(
    int ProcessedCount,
    bool HasRemainingQueuedRequests);

public sealed class BackfillRequestsCommandService(
    IBackfillRequestRepository repository,
    IBackfillRequestProjection projection)
{
    public async Task RequestAsync(RequestBackfillCommand command, CancellationToken cancellationToken)
    {
        var backfillRequestId = new BackfillRequestId(command.BackfillRequestId);
        var existing = await repository.GetAsync(backfillRequestId, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Backfill request '{command.BackfillRequestId}' has already been created.");
        }

        var backfillRequest = BackfillRequest.Request(
            backfillRequestId,
            command.Source,
            command.Scope,
            command.RequestedFrom,
            command.RequestedTo,
            command.RequestedBy,
            command.RequestedAtUtc);

        await SaveAndProjectAsync(
            backfillRequest,
            [
                new BackfillRequestedIntegrationEvent(
                    command.CommandId,
                    command.RequestedAtUtc,
                    command.BackfillRequestId,
                    command.Source.Value,
                    command.Scope.Value,
                    command.RequestedFrom,
                    command.RequestedTo,
                    command.RequestedBy)
            ],
            cancellationToken);
    }

    public async Task MarkStartedAsync(MarkBackfillStartedCommand command, CancellationToken cancellationToken)
    {
        var backfillRequest = await repository.GetAsync(new BackfillRequestId(command.BackfillRequestId), cancellationToken)
            ?? throw new InvalidOperationException($"Backfill request '{command.BackfillRequestId}' was not found.");

        backfillRequest.MarkStarted(command.RequestedAtUtc);
        await SaveAndProjectAsync(backfillRequest, [], cancellationToken);
    }

    public async Task MarkCompletedAsync(MarkBackfillCompletedCommand command, CancellationToken cancellationToken)
    {
        var backfillRequest = await repository.GetAsync(new BackfillRequestId(command.BackfillRequestId), cancellationToken)
            ?? throw new InvalidOperationException($"Backfill request '{command.BackfillRequestId}' was not found.");

        backfillRequest.MarkCompleted(command.RequestedAtUtc);
        await SaveAndProjectAsync(backfillRequest, [], cancellationToken);
    }

    private async Task SaveAndProjectAsync(
        BackfillRequest backfillRequest,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        var pendingEvents = backfillRequest.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        if (repository is IBackfillRequestOutboxWriter outboxWriter && integrationEvents.Count != 0)
        {
            await outboxWriter.SaveAsync(backfillRequest, integrationEvents, cancellationToken);
        }
        else
        {
            await repository.SaveAsync(backfillRequest, cancellationToken);
        }

        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }
}

public sealed class BackfillRequestsQueryService(IBackfillRequestReadRepository repository)
{
    public async Task<IReadOnlyList<BackfillRequestResponse>> GetBackfillsAsync(CancellationToken cancellationToken)
    {
        var backfills = await repository.GetBackfillsAsync(cancellationToken);

        return backfills
            .OrderByDescending(backfill => backfill.RequestedAtUtc)
            .ThenBy(backfill => backfill.Source, StringComparer.OrdinalIgnoreCase)
            .Select(backfill => new BackfillRequestResponse(
                backfill.Id,
                backfill.Source,
                backfill.Scope,
                backfill.Status,
                backfill.RequestedBy,
                backfill.RequestedFrom,
                backfill.RequestedTo,
                backfill.RequestedAtUtc,
                backfill.StartedAtUtc,
                backfill.CompletedAtUtc))
            .ToArray();
    }
}

public sealed class BackfillExecutionService(
    IBackfillRequestRepository repository,
    IBackfillRequestProjection projection)
{
    public async Task<BackfillExecutionResult> ProcessNextQueuedAsync(CancellationToken cancellationToken)
    {
        var backfillRequest = await repository.GetNextQueuedAsync(cancellationToken);
        if (backfillRequest is null)
        {
            return new BackfillExecutionResult(false, null, null);
        }

        return await ProcessAsync(backfillRequest.Id, cancellationToken);
    }

    public async Task<BackfillExecutionResult> ProcessAsync(BackfillRequestId backfillRequestId, CancellationToken cancellationToken)
    {
        var backfillRequest = await repository.GetAsync(backfillRequestId, cancellationToken);
        if (backfillRequest is null)
        {
            return new BackfillExecutionResult(false, backfillRequestId.Value, null);
        }

        if (!string.Equals(backfillRequest.Status.Code, "queued", StringComparison.OrdinalIgnoreCase))
        {
            return new BackfillExecutionResult(false, backfillRequest.Id.Value, backfillRequest.Status.Code);
        }

        backfillRequest.MarkStarted(DateTimeOffset.UtcNow);
        await SaveAndProjectAsync(backfillRequest, cancellationToken);

        backfillRequest.MarkCompleted(DateTimeOffset.UtcNow);
        await SaveAndProjectAsync(backfillRequest, cancellationToken);

        return new BackfillExecutionResult(true, backfillRequest.Id.Value, backfillRequest.Status.Code);
    }

    private async Task SaveAndProjectAsync(BackfillRequest backfillRequest, CancellationToken cancellationToken)
    {
        var pendingEvents = backfillRequest.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        await repository.SaveAsync(backfillRequest, cancellationToken);
        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }
}

public sealed class BackfillQueueProcessor(
    IBackfillRequestRepository repository,
    BackfillExecutionService executionService,
    IOutboxMessageStore? outboxMessageStore = null,
    IInboxStore? inboxStore = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] RequestedMessageTypes = [typeof(BackfillRequestedIntegrationEvent).FullName ?? nameof(BackfillRequestedIntegrationEvent)];
    public const string ConsumerName = "worker-lite.backfill-requested";

    public async Task<BackfillBatchProcessingResult> ProcessAvailableAsync(int maxTasks, CancellationToken cancellationToken)
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
            if (!executionResult.HasProcessedRequest)
            {
                return new BackfillBatchProcessingResult(processedCount, false);
            }

            processedCount++;
        }

        var hasRemainingQueuedRequests = await repository.GetNextQueuedAsync(cancellationToken) is not null;
        return new BackfillBatchProcessingResult(processedCount, hasRemainingQueuedRequests);
    }

    private async Task<BackfillBatchProcessingResult> ProcessFromOutboxAsync(int maxTasks, CancellationToken cancellationToken)
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
                var integrationEvent = JsonSerializer.Deserialize<BackfillRequestedIntegrationEvent>(message.Payload, SerializerOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(BackfillRequestedIntegrationEvent)}'.");

                await executionService.ProcessAsync(new BackfillRequestId(integrationEvent.BackfillRequestId), cancellationToken);
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

        var hasRemainingQueuedRequests = (await outboxMessageStore.GetPendingAsync(RequestedMessageTypes, 1, cancellationToken)).Count != 0;
        return new BackfillBatchProcessingResult(processedCount, hasRemainingQueuedRequests);
    }
}
