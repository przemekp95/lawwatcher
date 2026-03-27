using LawWatcher.BuildingBlocks.Domain;

namespace LawWatcher.TaxonomyAndProfiles.Domain.MonitoringProfiles;

public sealed record MonitoringProfileId : ValueObject
{
    public MonitoringProfileId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Monitoring profile identifier cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public sealed record ProfileRule(string Kind, string Value) : ValueObject
{
    public static ProfileRule Keyword(string value)
    {
        var normalizedValue = NormalizeValue(value);
        return new ProfileRule("keyword", normalizedValue);
    }

    private static string NormalizeValue(string value)
    {
        var normalizedValue = value.Trim();
        if (normalizedValue.Length == 0)
        {
            throw new ArgumentException("Profile rule value cannot be empty.", nameof(value));
        }

        return normalizedValue;
    }
}

public sealed record AlertPolicy(string Code, TimeSpan? DigestInterval) : ValueObject
{
    public static AlertPolicy Immediate() => new("immediate", null);

    public static AlertPolicy Digest(TimeSpan digestInterval)
    {
        if (digestInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(digestInterval), "Digest interval must be greater than zero.");
        }

        return new AlertPolicy("digest", digestInterval);
    }
}

public sealed record MonitoringProfileCreated(
    MonitoringProfileId ProfileId,
    string Name,
    string AlertPolicyCode,
    TimeSpan? DigestInterval,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record MonitoringProfileRuleAdded(
    MonitoringProfileId ProfileId,
    string RuleKind,
    string RuleValue,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record MonitoringProfileAlertPolicyChanged(
    MonitoringProfileId ProfileId,
    string AlertPolicyCode,
    TimeSpan? DigestInterval,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record MonitoringProfileDeactivated(
    MonitoringProfileId ProfileId,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed class MonitoringProfile : AggregateRoot<MonitoringProfileId>
{
    private readonly List<ProfileRule> _rules = [];

    private string _name = string.Empty;
    private AlertPolicy _alertPolicy = AlertPolicy.Digest(TimeSpan.FromHours(12));
    private bool _isActive;

    private MonitoringProfile()
    {
    }

    public string Name => _name;

    public AlertPolicy AlertPolicy => _alertPolicy;

    public bool IsActive => _isActive;

    public IReadOnlyCollection<ProfileRule> Rules => _rules.AsReadOnly();

    public static MonitoringProfile Create(
        MonitoringProfileId id,
        string name,
        AlertPolicy alertPolicy,
        DateTimeOffset createdAtUtc)
    {
        var profile = new MonitoringProfile();
        profile.Raise(new MonitoringProfileCreated(
            id,
            NormalizeName(name),
            alertPolicy.Code,
            alertPolicy.DigestInterval,
            createdAtUtc));
        return profile;
    }

    public static MonitoringProfile Rehydrate(IEnumerable<IDomainEvent> history)
    {
        var profile = new MonitoringProfile();
        profile.LoadFromHistory(history);
        return profile;
    }

    public void AddRule(ProfileRule rule, DateTimeOffset changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(rule);
        EnsureActive();

        if (_rules.Contains(rule))
        {
            return;
        }

        Raise(new MonitoringProfileRuleAdded(Id, rule.Kind, rule.Value, changedAtUtc));
    }

    public void ChangeAlertPolicy(AlertPolicy alertPolicy, DateTimeOffset changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(alertPolicy);
        EnsureActive();

        if (_alertPolicy == alertPolicy)
        {
            return;
        }

        Raise(new MonitoringProfileAlertPolicyChanged(Id, alertPolicy.Code, alertPolicy.DigestInterval, changedAtUtc));
    }

    public void Deactivate(DateTimeOffset changedAtUtc)
    {
        if (!_isActive)
        {
            return;
        }

        Raise(new MonitoringProfileDeactivated(Id, changedAtUtc));
    }

    protected override void Apply(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case MonitoringProfileCreated created:
                Id = created.ProfileId;
                _name = created.Name;
                _alertPolicy = RehydrateAlertPolicy(created.AlertPolicyCode, created.DigestInterval);
                _isActive = true;
                break;
            case MonitoringProfileRuleAdded ruleAdded:
                _rules.Add(new ProfileRule(ruleAdded.RuleKind, ruleAdded.RuleValue));
                break;
            case MonitoringProfileAlertPolicyChanged alertPolicyChanged:
                _alertPolicy = RehydrateAlertPolicy(alertPolicyChanged.AlertPolicyCode, alertPolicyChanged.DigestInterval);
                break;
            case MonitoringProfileDeactivated:
                _isActive = false;
                break;
            default:
                throw new InvalidOperationException($"Unsupported domain event type '{domainEvent.GetType().Name}' for monitoring profile.");
        }
    }

    private void EnsureActive()
    {
        if (!_isActive)
        {
            throw new InvalidOperationException($"Monitoring profile '{Id.Value:D}' is inactive.");
        }
    }

    private static string NormalizeName(string name)
    {
        var normalizedName = name.Trim();
        if (normalizedName.Length == 0)
        {
            throw new ArgumentException("Monitoring profile name cannot be empty.", nameof(name));
        }

        return normalizedName;
    }

    private static AlertPolicy RehydrateAlertPolicy(string code, TimeSpan? digestInterval)
    {
        return code switch
        {
            "immediate" => AlertPolicy.Immediate(),
            "digest" when digestInterval.HasValue => AlertPolicy.Digest(digestInterval.Value),
            _ => throw new InvalidOperationException($"Unsupported alert policy code '{code}'.")
        };
    }
}
