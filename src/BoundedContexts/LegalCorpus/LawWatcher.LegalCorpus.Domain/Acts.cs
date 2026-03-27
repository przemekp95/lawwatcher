using LawWatcher.BuildingBlocks.Domain;

namespace LawWatcher.LegalCorpus.Domain.Acts;

public sealed record ActId : ValueObject
{
    public ActId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Act identifier cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public sealed record OriginatingBillReference(Guid BillId, string BillTitle, string BillExternalId) : ValueObject
{
    public static OriginatingBillReference Create(Guid billId, string billTitle, string billExternalId)
    {
        if (billId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(billId), "Originating bill identifier cannot be empty.");
        }

        return new OriginatingBillReference(
            billId,
            NormalizeRequired(billTitle, nameof(billTitle)),
            NormalizeRequired(billExternalId, nameof(billExternalId)));
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Value cannot be empty.", paramName);
        }

        return normalized;
    }
}

public sealed record EliReference : ValueObject
{
    private EliReference(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static EliReference Create(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("ELI reference cannot be empty.", nameof(value));
        }

        return new EliReference(normalized);
    }

    public override string ToString() => Value;
}

public sealed record ActArtifact(string Kind, string ObjectKey) : ValueObject
{
    public static ActArtifact Create(string kind, string objectKey)
    {
        return new ActArtifact(
            NormalizeRequired(kind, nameof(kind)),
            NormalizeRequired(objectKey, nameof(objectKey)));
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Value cannot be empty.", paramName);
        }

        return normalized;
    }
}

public sealed record PublishedActRegistered(
    ActId ActId,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    string Eli,
    string Title,
    DateOnly PublishedOn,
    DateOnly? EffectiveFrom,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record ActArtifactAttached(
    ActId ActId,
    string Kind,
    string ObjectKey,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed class PublishedAct : AggregateRoot<ActId>
{
    private readonly List<ActArtifact> _artifacts = [];

    private OriginatingBillReference _originatingBill = OriginatingBillReference.Create(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "placeholder",
        "placeholder");
    private EliReference _eli = EliReference.Create("https://eli.gov.pl/eli/DU/2000/1/ogl");
    private string _title = string.Empty;

    private PublishedAct()
    {
    }

    public OriginatingBillReference OriginatingBill => _originatingBill;

    public EliReference Eli => _eli;

    public string Title => _title;

    public DateOnly PublishedOn { get; private set; }

    public DateOnly? EffectiveFrom { get; private set; }

    public IReadOnlyCollection<ActArtifact> Artifacts => _artifacts.AsReadOnly();

    public static PublishedAct Register(
        ActId id,
        OriginatingBillReference originatingBill,
        EliReference eli,
        string title,
        DateOnly publishedOn,
        DateOnly? effectiveFrom,
        DateTimeOffset occurredAtUtc)
    {
        var act = new PublishedAct();
        act.Raise(new PublishedActRegistered(
            id,
            originatingBill.BillId,
            originatingBill.BillTitle,
            originatingBill.BillExternalId,
            eli.Value,
            NormalizeTitle(title),
            publishedOn,
            effectiveFrom,
            occurredAtUtc));
        return act;
    }

    public static PublishedAct Rehydrate(IEnumerable<IDomainEvent> history)
    {
        var act = new PublishedAct();
        act.LoadFromHistory(history);
        return act;
    }

    public void AttachArtifact(ActArtifact artifact, DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (_artifacts.Contains(artifact))
        {
            return;
        }

        Raise(new ActArtifactAttached(Id, artifact.Kind, artifact.ObjectKey, occurredAtUtc));
    }

    protected override void Apply(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case PublishedActRegistered registered:
                Id = registered.ActId;
                _originatingBill = OriginatingBillReference.Create(
                    registered.BillId,
                    registered.BillTitle,
                    registered.BillExternalId);
                _eli = EliReference.Create(registered.Eli);
                _title = registered.Title;
                PublishedOn = registered.PublishedOn;
                EffectiveFrom = registered.EffectiveFrom;
                break;
            case ActArtifactAttached attached:
                _artifacts.Add(ActArtifact.Create(attached.Kind, attached.ObjectKey));
                break;
            default:
                throw new InvalidOperationException($"Unsupported domain event type '{domainEvent.GetType().Name}' for published act.");
        }
    }

    private static string NormalizeTitle(string title)
    {
        var normalized = title.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Act title cannot be empty.", nameof(title));
        }

        return normalized;
    }
}
