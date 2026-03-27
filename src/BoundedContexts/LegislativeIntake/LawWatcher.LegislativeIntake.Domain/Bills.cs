using LawWatcher.BuildingBlocks.Domain;

namespace LawWatcher.LegislativeIntake.Domain.Bills;

public sealed record BillId : ValueObject
{
    public BillId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Bill identifier cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public sealed record ExternalBillReference(string SourceSystem, string ExternalId, string SourceUrl) : ValueObject
{
    public static ExternalBillReference Create(string sourceSystem, string externalId, string sourceUrl)
    {
        return new ExternalBillReference(
            NormalizeRequired(sourceSystem, nameof(sourceSystem)),
            NormalizeRequired(externalId, nameof(externalId)),
            NormalizeRequired(sourceUrl, nameof(sourceUrl)));
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

public sealed record BillDocument(string Kind, string ObjectKey) : ValueObject
{
    public static BillDocument Create(string kind, string objectKey)
    {
        return new BillDocument(
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

public sealed record BillImported(
    BillId BillId,
    string SourceSystem,
    string ExternalId,
    string SourceUrl,
    string Title,
    DateOnly SubmittedOn,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record BillDocumentAttached(
    BillId BillId,
    string Kind,
    string ObjectKey,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed class ImportedBill : AggregateRoot<BillId>
{
    private readonly List<BillDocument> _documents = [];

    private ExternalBillReference _externalReference = ExternalBillReference.Create("unknown", "unknown", "https://localhost");
    private string _title = string.Empty;

    private ImportedBill()
    {
    }

    public ExternalBillReference ExternalReference => _externalReference;

    public string Title => _title;

    public DateOnly SubmittedOn { get; private set; }

    public IReadOnlyCollection<BillDocument> Documents => _documents.AsReadOnly();

    public static ImportedBill Import(
        BillId id,
        ExternalBillReference externalReference,
        string title,
        DateOnly submittedOn,
        DateTimeOffset occurredAtUtc)
    {
        var bill = new ImportedBill();
        bill.Raise(new BillImported(
            id,
            externalReference.SourceSystem,
            externalReference.ExternalId,
            externalReference.SourceUrl,
            NormalizeTitle(title),
            submittedOn,
            occurredAtUtc));
        return bill;
    }

    public static ImportedBill Rehydrate(IEnumerable<IDomainEvent> history)
    {
        var bill = new ImportedBill();
        bill.LoadFromHistory(history);
        return bill;
    }

    public void AttachDocument(BillDocument document, DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (_documents.Contains(document))
        {
            return;
        }

        Raise(new BillDocumentAttached(Id, document.Kind, document.ObjectKey, occurredAtUtc));
    }

    protected override void Apply(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case BillImported imported:
                Id = imported.BillId;
                _externalReference = ExternalBillReference.Create(imported.SourceSystem, imported.ExternalId, imported.SourceUrl);
                _title = imported.Title;
                SubmittedOn = imported.SubmittedOn;
                break;
            case BillDocumentAttached attached:
                _documents.Add(BillDocument.Create(attached.Kind, attached.ObjectKey));
                break;
            default:
                throw new InvalidOperationException($"Unsupported domain event type '{domainEvent.GetType().Name}' for imported bill.");
        }
    }

    private static string NormalizeTitle(string title)
    {
        var normalized = title.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Bill title cannot be empty.", nameof(title));
        }

        return normalized;
    }
}
