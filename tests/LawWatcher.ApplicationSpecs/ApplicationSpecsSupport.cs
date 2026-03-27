using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegislativeIntake.Application;
using LawWatcher.LegislativeProcess.Application;
using LawWatcher.TaxonomyAndProfiles.Application;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http;

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

internal sealed class StubBrokerDiagnosticsStore(BrokerDiagnosticsSnapshot snapshot) : IBrokerDiagnosticsStore
{
    public bool IsAvailable => snapshot.IsAvailable;

    public Task<BrokerDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
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

internal sealed class StaticOptionsMonitor<TOptions>(TOptions currentValue) : IOptionsMonitor<TOptions>
{
    public TOptions CurrentValue => currentValue;

    public TOptions Get(string? name) => currentValue;

    public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
}
