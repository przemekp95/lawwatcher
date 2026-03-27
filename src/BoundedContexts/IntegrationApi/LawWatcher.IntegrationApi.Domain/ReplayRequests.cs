using LawWatcher.BuildingBlocks.Domain;

namespace LawWatcher.IntegrationApi.Domain.Replays;

public sealed record ReplayRequestId : ValueObject
{
    public ReplayRequestId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Replay request identifier cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public sealed record ReplayScope : ValueObject
{
    private ReplayScope(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ReplayScope Of(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Replay scope cannot be empty.", nameof(value));
        }

        return new ReplayScope(normalized);
    }

    public override string ToString() => Value;
}

public sealed record ReplayStatus : ValueObject
{
    private ReplayStatus(string code)
    {
        Code = code;
    }

    public string Code { get; }

    public static ReplayStatus Queued() => new("queued");

    public static ReplayStatus Running() => new("running");

    public static ReplayStatus Completed() => new("completed");
}

public sealed record ReplayRequested(
    ReplayRequestId ReplayRequestId,
    string Scope,
    string RequestedBy,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record ReplayStarted(
    ReplayRequestId ReplayRequestId,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record ReplayCompleted(
    ReplayRequestId ReplayRequestId,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed class ReplayRequest : AggregateRoot<ReplayRequestId>
{
    private ReplayScope _scope = ReplayScope.Of("placeholder");
    private string _requestedBy = "system";
    private ReplayStatus _status = ReplayStatus.Queued();

    private ReplayRequest()
    {
    }

    public ReplayScope Scope => _scope;

    public string RequestedBy => _requestedBy;

    public ReplayStatus Status => _status;

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static ReplayRequest Request(
        ReplayRequestId id,
        ReplayScope scope,
        string requestedBy,
        DateTimeOffset occurredAtUtc)
    {
        var replay = new ReplayRequest();
        replay.Raise(new ReplayRequested(
            id,
            scope.Value,
            NormalizeRequestedBy(requestedBy),
            occurredAtUtc));
        return replay;
    }

    public static ReplayRequest Rehydrate(IEnumerable<IDomainEvent> history)
    {
        var replay = new ReplayRequest();
        replay.LoadFromHistory(history);
        return replay;
    }

    public void MarkStarted(DateTimeOffset occurredAtUtc)
    {
        if (_status.Code is "running" or "completed")
        {
            return;
        }

        Raise(new ReplayStarted(Id, occurredAtUtc));
    }

    public void MarkCompleted(DateTimeOffset occurredAtUtc)
    {
        if (_status.Code == "completed")
        {
            return;
        }

        Raise(new ReplayCompleted(Id, occurredAtUtc));
    }

    protected override void Apply(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case ReplayRequested requested:
                Id = requested.ReplayRequestId;
                _scope = ReplayScope.Of(requested.Scope);
                _requestedBy = requested.RequestedBy;
                _status = ReplayStatus.Queued();
                RequestedAtUtc = requested.OccurredAtUtc;
                break;
            case ReplayStarted started:
                _status = ReplayStatus.Running();
                StartedAtUtc = started.OccurredAtUtc;
                break;
            case ReplayCompleted completed:
                _status = ReplayStatus.Completed();
                CompletedAtUtc = completed.OccurredAtUtc;
                if (StartedAtUtc is null)
                {
                    StartedAtUtc = completed.OccurredAtUtc;
                }
                break;
            default:
                throw new InvalidOperationException($"Unsupported domain event type '{domainEvent.GetType().Name}' for replay request.");
        }
    }

    private static string NormalizeRequestedBy(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Replay requester cannot be empty.", nameof(value));
        }

        return normalized;
    }
}
