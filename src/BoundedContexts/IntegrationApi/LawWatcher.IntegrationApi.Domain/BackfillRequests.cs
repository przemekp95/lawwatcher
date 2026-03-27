using LawWatcher.BuildingBlocks.Domain;

namespace LawWatcher.IntegrationApi.Domain.Backfills;

public sealed record BackfillRequestId : ValueObject
{
    public BackfillRequestId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Backfill request identifier cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public sealed record BackfillSource : ValueObject
{
    private BackfillSource(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static BackfillSource Of(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Backfill source cannot be empty.", nameof(value));
        }

        return new BackfillSource(normalized);
    }

    public override string ToString() => Value;
}

public sealed record BackfillScope : ValueObject
{
    private BackfillScope(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static BackfillScope Of(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Backfill scope cannot be empty.", nameof(value));
        }

        return new BackfillScope(normalized);
    }

    public override string ToString() => Value;
}

public sealed record BackfillStatus : ValueObject
{
    private BackfillStatus(string code)
    {
        Code = code;
    }

    public string Code { get; }

    public static BackfillStatus Queued() => new("queued");

    public static BackfillStatus Running() => new("running");

    public static BackfillStatus Completed() => new("completed");
}

public sealed record BackfillRequested(
    BackfillRequestId BackfillRequestId,
    string Source,
    string Scope,
    DateOnly RequestedFrom,
    DateOnly? RequestedTo,
    string RequestedBy,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record BackfillStarted(
    BackfillRequestId BackfillRequestId,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record BackfillCompleted(
    BackfillRequestId BackfillRequestId,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed class BackfillRequest : AggregateRoot<BackfillRequestId>
{
    private BackfillSource _source = BackfillSource.Of("placeholder");
    private BackfillScope _scope = BackfillScope.Of("placeholder");
    private string _requestedBy = "system";
    private BackfillStatus _status = BackfillStatus.Queued();

    private BackfillRequest()
    {
    }

    public BackfillSource Source => _source;

    public BackfillScope Scope => _scope;

    public string RequestedBy => _requestedBy;

    public BackfillStatus Status => _status;

    public DateOnly RequestedFrom { get; private set; }

    public DateOnly? RequestedTo { get; private set; }

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static BackfillRequest Request(
        BackfillRequestId id,
        BackfillSource source,
        BackfillScope scope,
        DateOnly requestedFrom,
        DateOnly? requestedTo,
        string requestedBy,
        DateTimeOffset occurredAtUtc)
    {
        if (requestedTo is not null && requestedTo < requestedFrom)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedTo), "Backfill end date cannot be earlier than the start date.");
        }

        var backfill = new BackfillRequest();
        backfill.Raise(new BackfillRequested(
            id,
            source.Value,
            scope.Value,
            requestedFrom,
            requestedTo,
            NormalizeRequestedBy(requestedBy),
            occurredAtUtc));
        return backfill;
    }

    public static BackfillRequest Rehydrate(IEnumerable<IDomainEvent> history)
    {
        var backfill = new BackfillRequest();
        backfill.LoadFromHistory(history);
        return backfill;
    }

    public void MarkStarted(DateTimeOffset occurredAtUtc)
    {
        if (_status.Code is "running" or "completed")
        {
            return;
        }

        Raise(new BackfillStarted(Id, occurredAtUtc));
    }

    public void MarkCompleted(DateTimeOffset occurredAtUtc)
    {
        if (_status.Code == "completed")
        {
            return;
        }

        Raise(new BackfillCompleted(Id, occurredAtUtc));
    }

    protected override void Apply(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case BackfillRequested requested:
                Id = requested.BackfillRequestId;
                _source = BackfillSource.Of(requested.Source);
                _scope = BackfillScope.Of(requested.Scope);
                RequestedFrom = requested.RequestedFrom;
                RequestedTo = requested.RequestedTo;
                _requestedBy = requested.RequestedBy;
                _status = BackfillStatus.Queued();
                RequestedAtUtc = requested.OccurredAtUtc;
                break;
            case BackfillStarted started:
                _status = BackfillStatus.Running();
                StartedAtUtc = started.OccurredAtUtc;
                break;
            case BackfillCompleted completed:
                _status = BackfillStatus.Completed();
                CompletedAtUtc = completed.OccurredAtUtc;
                StartedAtUtc ??= completed.OccurredAtUtc;
                break;
            default:
                throw new InvalidOperationException($"Unsupported domain event type '{domainEvent.GetType().Name}' for backfill request.");
        }
    }

    private static string NormalizeRequestedBy(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Backfill requester cannot be empty.", nameof(value));
        }

        return normalized;
    }
}
