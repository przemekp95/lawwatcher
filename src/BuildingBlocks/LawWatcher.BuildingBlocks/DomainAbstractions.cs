namespace LawWatcher.BuildingBlocks.Domain;

public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAtUtc { get; }
}

public abstract record DomainEvent(Guid EventId, DateTimeOffset OccurredAtUtc) : IDomainEvent
{
    protected DomainEvent() : this(Guid.NewGuid(), DateTimeOffset.UtcNow)
    {
    }
}

public abstract class AggregateRoot<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _uncommittedEvents = [];

    public TId Id { get; protected set; } = default!;

    public long Version { get; private set; }

    public IReadOnlyCollection<IDomainEvent> UncommittedEvents => _uncommittedEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent)
    {
        Apply(domainEvent);
        _uncommittedEvents.Add(domainEvent);
        Version++;
    }

    public void LoadFromHistory(IEnumerable<IDomainEvent> eventHistory)
    {
        foreach (var domainEvent in eventHistory)
        {
            Apply(domainEvent);
            Version++;
        }

        _uncommittedEvents.Clear();
    }

    public IReadOnlyCollection<IDomainEvent> DequeueUncommittedEvents()
    {
        var snapshot = _uncommittedEvents.ToArray();
        _uncommittedEvents.Clear();
        return snapshot;
    }

    protected abstract void Apply(IDomainEvent domainEvent);
}

public abstract record ValueObject;
