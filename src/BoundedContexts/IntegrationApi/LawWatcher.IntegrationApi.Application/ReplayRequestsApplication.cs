using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Contracts;
using LawWatcher.IntegrationApi.Domain.Replays;
using System.Text.Json;

namespace LawWatcher.IntegrationApi.Application;

public sealed record RequestReplayCommand(
    Guid ReplayRequestId,
    ReplayScope Scope,
    string RequestedBy) : Command;

public sealed record MarkReplayStartedCommand(
    Guid ReplayRequestId) : Command;

public sealed record MarkReplayCompletedCommand(
    Guid ReplayRequestId) : Command;

public sealed record ReplayRequestReadModel(
    Guid Id,
    string Scope,
    string Status,
    string RequestedBy,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public interface IReplayRequestRepository
{
    Task<ReplayRequest?> GetAsync(ReplayRequestId id, CancellationToken cancellationToken);

    Task<ReplayRequest?> GetNextQueuedAsync(CancellationToken cancellationToken);

    Task SaveAsync(ReplayRequest replayRequest, CancellationToken cancellationToken);
}

public interface IReplayRequestOutboxWriter
{
    Task SaveAsync(
        ReplayRequest replayRequest,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken);
}

public interface IReplayRequestReadRepository
{
    Task<IReadOnlyCollection<ReplayRequestReadModel>> GetReplaysAsync(CancellationToken cancellationToken);
}

public interface IReplayRequestProjection
{
    Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}

public sealed record ReplayExecutionResult(
    bool HasProcessedRequest,
    Guid? ReplayRequestId,
    string? Status);

public sealed record ReplayBatchProcessingResult(
    int ProcessedCount,
    bool HasRemainingQueuedRequests);

public sealed class ReplayRequestsCommandService(
    IReplayRequestRepository repository,
    IReplayRequestProjection projection)
{
    public async Task RequestAsync(RequestReplayCommand command, CancellationToken cancellationToken)
    {
        var replayRequestId = new ReplayRequestId(command.ReplayRequestId);
        var existing = await repository.GetAsync(replayRequestId, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Replay request '{command.ReplayRequestId}' has already been created.");
        }

        var replayRequest = ReplayRequest.Request(
            replayRequestId,
            command.Scope,
            command.RequestedBy,
            command.RequestedAtUtc);

        await SaveAndProjectAsync(
            replayRequest,
            [
                new ReplayRequestedIntegrationEvent(
                    command.CommandId,
                    command.RequestedAtUtc,
                    command.ReplayRequestId,
                    command.Scope.Value,
                    command.RequestedBy)
            ],
            cancellationToken);
    }

    public async Task MarkStartedAsync(MarkReplayStartedCommand command, CancellationToken cancellationToken)
    {
        var replayRequest = await repository.GetAsync(new ReplayRequestId(command.ReplayRequestId), cancellationToken)
            ?? throw new InvalidOperationException($"Replay request '{command.ReplayRequestId}' was not found.");

        replayRequest.MarkStarted(command.RequestedAtUtc);
        await SaveAndProjectAsync(replayRequest, [], cancellationToken);
    }

    public async Task MarkCompletedAsync(MarkReplayCompletedCommand command, CancellationToken cancellationToken)
    {
        var replayRequest = await repository.GetAsync(new ReplayRequestId(command.ReplayRequestId), cancellationToken)
            ?? throw new InvalidOperationException($"Replay request '{command.ReplayRequestId}' was not found.");

        replayRequest.MarkCompleted(command.RequestedAtUtc);
        await SaveAndProjectAsync(replayRequest, [], cancellationToken);
    }

    private async Task SaveAndProjectAsync(
        ReplayRequest replayRequest,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        var pendingEvents = replayRequest.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        if (repository is IReplayRequestOutboxWriter outboxWriter && integrationEvents.Count != 0)
        {
            await outboxWriter.SaveAsync(replayRequest, integrationEvents, cancellationToken);
        }
        else
        {
            await repository.SaveAsync(replayRequest, cancellationToken);
        }

        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }
}

public sealed class ReplayRequestsQueryService(IReplayRequestReadRepository repository)
{
    public async Task<IReadOnlyList<ReplayRequestResponse>> GetReplaysAsync(CancellationToken cancellationToken)
    {
        var replays = await repository.GetReplaysAsync(cancellationToken);

        return replays
            .OrderByDescending(replay => replay.RequestedAtUtc)
            .ThenBy(replay => replay.Scope, StringComparer.OrdinalIgnoreCase)
            .Select(replay => new ReplayRequestResponse(
                replay.Id,
                replay.Scope,
                replay.Status,
                replay.RequestedBy,
                replay.RequestedAtUtc,
                replay.StartedAtUtc,
                replay.CompletedAtUtc))
            .ToArray();
    }
}

public sealed class ReplayExecutionService(
    IReplayRequestRepository repository,
    IReplayRequestProjection projection)
{
    public async Task<ReplayExecutionResult> ProcessNextQueuedAsync(CancellationToken cancellationToken)
    {
        var replayRequest = await repository.GetNextQueuedAsync(cancellationToken);
        if (replayRequest is null)
        {
            return new ReplayExecutionResult(false, null, null);
        }

        return await ProcessAsync(replayRequest.Id, cancellationToken);
    }

    public async Task<ReplayExecutionResult> ProcessAsync(ReplayRequestId replayRequestId, CancellationToken cancellationToken)
    {
        var replayRequest = await repository.GetAsync(replayRequestId, cancellationToken);
        if (replayRequest is null)
        {
            return new ReplayExecutionResult(false, replayRequestId.Value, null);
        }

        if (!string.Equals(replayRequest.Status.Code, "queued", StringComparison.OrdinalIgnoreCase))
        {
            return new ReplayExecutionResult(false, replayRequest.Id.Value, replayRequest.Status.Code);
        }

        replayRequest.MarkStarted(DateTimeOffset.UtcNow);
        await SaveAndProjectAsync(replayRequest, cancellationToken);

        replayRequest.MarkCompleted(DateTimeOffset.UtcNow);
        await SaveAndProjectAsync(replayRequest, cancellationToken);

        return new ReplayExecutionResult(true, replayRequest.Id.Value, replayRequest.Status.Code);
    }

    private async Task SaveAndProjectAsync(ReplayRequest replayRequest, CancellationToken cancellationToken)
    {
        var pendingEvents = replayRequest.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        await repository.SaveAsync(replayRequest, cancellationToken);
        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }
}

public sealed class ReplayQueueProcessor(
    IReplayRequestRepository repository,
    ReplayExecutionService executionService,
    IOutboxMessageStore? outboxMessageStore = null,
    IInboxStore? inboxStore = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] RequestedMessageTypes = [typeof(ReplayRequestedIntegrationEvent).FullName ?? nameof(ReplayRequestedIntegrationEvent)];
    public const string ConsumerName = "worker-lite.replay-requested";

    public async Task<ReplayBatchProcessingResult> ProcessAvailableAsync(int maxTasks, CancellationToken cancellationToken)
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
                return new ReplayBatchProcessingResult(processedCount, false);
            }

            processedCount++;
        }

        var hasRemainingQueuedRequests = await repository.GetNextQueuedAsync(cancellationToken) is not null;
        return new ReplayBatchProcessingResult(processedCount, hasRemainingQueuedRequests);
    }

    private async Task<ReplayBatchProcessingResult> ProcessFromOutboxAsync(int maxTasks, CancellationToken cancellationToken)
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
                var integrationEvent = JsonSerializer.Deserialize<ReplayRequestedIntegrationEvent>(message.Payload, SerializerOptions)
                    ?? throw new InvalidOperationException($"Unable to deserialize outbox message '{message.MessageId}' as '{nameof(ReplayRequestedIntegrationEvent)}'.");

                await executionService.ProcessAsync(new ReplayRequestId(integrationEvent.ReplayRequestId), cancellationToken);
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
        return new ReplayBatchProcessingResult(processedCount, hasRemainingQueuedRequests);
    }
}
