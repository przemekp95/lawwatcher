using LawWatcher.BuildingBlocks.Domain;

namespace LawWatcher.Notifications.Domain.BillAlerts;

public sealed record AlertId : ValueObject
{
    public AlertId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Alert identifier cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public sealed record AlertPolicySnapshot(string Code) : ValueObject
{
    public static AlertPolicySnapshot Create(string code)
    {
        var normalized = code.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Alert policy code cannot be empty.", nameof(code));
        }

        return new AlertPolicySnapshot(normalized);
    }
}

public sealed record AlertKeyword(string Value) : ValueObject
{
    public static AlertKeyword Create(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Alert keyword cannot be empty.", nameof(value));
        }

        return new AlertKeyword(normalized);
    }
}

public sealed record BillAlertCreated(
    AlertId AlertId,
    Guid ProfileId,
    string ProfileName,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    DateOnly BillSubmittedOn,
    string AlertPolicy,
    IReadOnlyCollection<string> MatchedKeywords,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed class BillAlert : AggregateRoot<AlertId>
{
    private readonly List<AlertKeyword> _matchedKeywords = [];

    private string _profileName = string.Empty;
    private string _billTitle = string.Empty;
    private string _billExternalId = string.Empty;
    private AlertPolicySnapshot _alertPolicy = AlertPolicySnapshot.Create("immediate");

    private BillAlert()
    {
    }

    public Guid ProfileId { get; private set; }

    public string ProfileName => _profileName;

    public Guid BillId { get; private set; }

    public string BillTitle => _billTitle;

    public string BillExternalId => _billExternalId;

    public DateOnly BillSubmittedOn { get; private set; }

    public AlertPolicySnapshot AlertPolicy => _alertPolicy;

    public IReadOnlyCollection<AlertKeyword> MatchedKeywords => _matchedKeywords.AsReadOnly();

    public static BillAlert Create(
        AlertId id,
        Guid profileId,
        string profileName,
        Guid billId,
        string billTitle,
        string billExternalId,
        DateOnly billSubmittedOn,
        AlertPolicySnapshot alertPolicy,
        IEnumerable<string> matchedKeywords,
        DateTimeOffset occurredAtUtc)
    {
        var normalizedProfileName = NormalizeRequired(profileName, nameof(profileName));
        var normalizedBillTitle = NormalizeRequired(billTitle, nameof(billTitle));
        var normalizedBillExternalId = NormalizeRequired(billExternalId, nameof(billExternalId));
        var normalizedKeywords = matchedKeywords
            .Select(AlertKeyword.Create)
            .Distinct()
            .OrderBy(keyword => keyword.Value, StringComparer.OrdinalIgnoreCase)
            .Select(keyword => keyword.Value)
            .ToArray();

        if (normalizedKeywords.Length == 0)
        {
            throw new InvalidOperationException("Bill alert requires at least one matched keyword.");
        }

        var alert = new BillAlert();
        alert.Raise(new BillAlertCreated(
            id,
            profileId,
            normalizedProfileName,
            billId,
            normalizedBillTitle,
            normalizedBillExternalId,
            billSubmittedOn,
            alertPolicy.Code,
            normalizedKeywords,
            occurredAtUtc));
        return alert;
    }

    public static BillAlert Rehydrate(IEnumerable<IDomainEvent> history)
    {
        var alert = new BillAlert();
        alert.LoadFromHistory(history);
        return alert;
    }

    protected override void Apply(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case BillAlertCreated created:
                Id = created.AlertId;
                ProfileId = created.ProfileId;
                _profileName = created.ProfileName;
                BillId = created.BillId;
                _billTitle = created.BillTitle;
                _billExternalId = created.BillExternalId;
                BillSubmittedOn = created.BillSubmittedOn;
                _alertPolicy = AlertPolicySnapshot.Create(created.AlertPolicy);
                _matchedKeywords.Clear();
                _matchedKeywords.AddRange(created.MatchedKeywords.Select(AlertKeyword.Create));
                break;
            default:
                throw new InvalidOperationException($"Unsupported domain event type '{domainEvent.GetType().Name}' for bill alert.");
        }
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
