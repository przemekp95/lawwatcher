using LawWatcher.BuildingBlocks.Domain;

namespace LawWatcher.BuildingBlocks.Messaging;

public interface ICommand
{
    Guid CommandId { get; }

    DateTimeOffset RequestedAtUtc { get; }
}

public abstract record Command(Guid CommandId, DateTimeOffset RequestedAtUtc) : ICommand
{
    protected Command() : this(Guid.NewGuid(), DateTimeOffset.UtcNow)
    {
    }
}

public interface IIntegrationEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAtUtc { get; }
}

public abstract record IntegrationEvent(Guid EventId, DateTimeOffset OccurredAtUtc) : IIntegrationEvent
{
    protected IntegrationEvent() : this(Guid.NewGuid(), DateTimeOffset.UtcNow)
    {
    }
}

public interface IEventStore
{
    Task AppendAsync(string streamId, string streamType, long expectedVersion, IReadOnlyCollection<IDomainEvent> events, CancellationToken cancellationToken);

    IAsyncEnumerable<IDomainEvent> ReadStreamAsync(string streamId, CancellationToken cancellationToken);
}

public interface IEventStoreWithOutbox : IEventStore
{
    Task AppendAsync(
        string streamId,
        string streamType,
        long expectedVersion,
        IReadOnlyCollection<IDomainEvent> events,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken);
}

public interface IOutboxStore
{
    Task EnqueueAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken);
}

public interface IIntegrationEventPublisher
{
    Task PublishAsync<TIntegrationEvent>(TIntegrationEvent integrationEvent, CancellationToken cancellationToken)
        where TIntegrationEvent : class, IIntegrationEvent;
}

public sealed record OutboxMessage(
    Guid MessageId,
    string MessageType,
    string Payload,
    string? Metadata,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? NextAttemptAtUtc);

public interface IOutboxMessageStore
{
    bool SupportsPolling { get; }

    Task<IReadOnlyCollection<OutboxMessage>> GetPendingAsync(
        IReadOnlyCollection<string> messageTypes,
        int maxCount,
        CancellationToken cancellationToken);

    Task MarkPublishedAsync(Guid messageId, DateTimeOffset publishedAtUtc, CancellationToken cancellationToken);

    Task DeferAsync(Guid messageId, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken);
}

public interface IInboxStore
{
    Task<bool> HasProcessedAsync(Guid messageId, string consumerName, CancellationToken cancellationToken);

    Task MarkProcessedAsync(Guid messageId, string consumerName, CancellationToken cancellationToken);
}

public sealed record OutboxMessageTypeDiagnosticsSnapshot(
    string MessageType,
    int TotalCount,
    int PendingCount,
    int ReadyCount,
    int DeferredCount,
    int PublishedCount,
    int MaxAttemptCount);

public sealed record OutboxDiagnosticsSnapshot(
    int TotalCount,
    int PendingCount,
    int ReadyCount,
    int DeferredCount,
    int PublishedCount,
    int MaxAttemptCount,
    DateTimeOffset? OldestPendingCreatedAtUtc,
    DateTimeOffset? NextScheduledAttemptAtUtc,
    IReadOnlyCollection<OutboxMessageTypeDiagnosticsSnapshot> MessageTypes);

public sealed record InboxConsumerDiagnosticsSnapshot(
    string ConsumerName,
    int ProcessedCount,
    DateTimeOffset? LastProcessedAtUtc);

public sealed record InboxDiagnosticsSnapshot(
    int ProcessedCount,
    IReadOnlyCollection<InboxConsumerDiagnosticsSnapshot> Consumers);

public sealed record MessagingDiagnosticsSnapshot(
    bool IsAvailable,
    OutboxDiagnosticsSnapshot Outbox,
    InboxDiagnosticsSnapshot Inbox);

public interface IMessagingDiagnosticsStore
{
    bool IsAvailable { get; }

    Task<MessagingDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

public sealed class EventStreamConcurrencyException : InvalidOperationException
{
    public EventStreamConcurrencyException(string streamId, long expectedVersion, long actualVersion)
        : base($"Optimistic concurrency check failed for stream '{streamId}'. Expected version {expectedVersion}, actual version {actualVersion}.")
    {
        StreamId = streamId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    public string StreamId { get; }

    public long ExpectedVersion { get; }

    public long ActualVersion { get; }
}
