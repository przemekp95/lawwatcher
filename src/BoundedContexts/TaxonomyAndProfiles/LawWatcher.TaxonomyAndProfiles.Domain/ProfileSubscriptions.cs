using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.TaxonomyAndProfiles.Domain.MonitoringProfiles;

namespace LawWatcher.TaxonomyAndProfiles.Domain.Subscriptions;

public sealed record ProfileSubscriptionId : ValueObject
{
    public ProfileSubscriptionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Profile subscription identifier cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public sealed record SubscribedProfileReference(Guid ProfileId, string ProfileName) : ValueObject
{
    public static SubscribedProfileReference Create(Guid profileId, string profileName)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(profileId), "Subscribed profile identifier cannot be empty.");
        }

        return new SubscribedProfileReference(profileId, NormalizeRequired(profileName, nameof(profileName)));
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

public sealed record SubscriberAddress : ValueObject
{
    private SubscriberAddress(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static SubscriberAddress Create(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Subscriber address cannot be empty.", nameof(value));
        }

        return new SubscriberAddress(normalized);
    }

    public override string ToString() => Value;
}

public sealed record SubscriptionChannel(string Code) : ValueObject
{
    public static SubscriptionChannel Email() => new("email");

    public static SubscriptionChannel Webhook() => new("webhook");
}

public sealed record ProfileSubscriptionCreated(
    ProfileSubscriptionId SubscriptionId,
    Guid ProfileId,
    string ProfileName,
    string Subscriber,
    string ChannelCode,
    string AlertPolicyCode,
    TimeSpan? DigestInterval,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record ProfileSubscriptionAlertPolicyChanged(
    ProfileSubscriptionId SubscriptionId,
    string AlertPolicyCode,
    TimeSpan? DigestInterval,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record ProfileSubscriptionDeactivated(
    ProfileSubscriptionId SubscriptionId,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed class ProfileSubscription : AggregateRoot<ProfileSubscriptionId>
{
    private SubscribedProfileReference _profile = SubscribedProfileReference.Create(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "placeholder");
    private SubscriberAddress _subscriber = SubscriberAddress.Create("placeholder@example.test");
    private SubscriptionChannel _channel = SubscriptionChannel.Email();
    private AlertPolicy _alertPolicy = AlertPolicy.Immediate();
    private bool _isActive;

    private ProfileSubscription()
    {
    }

    public SubscribedProfileReference Profile => _profile;

    public SubscriberAddress Subscriber => _subscriber;

    public SubscriptionChannel Channel => _channel;

    public AlertPolicy AlertPolicy => _alertPolicy;

    public bool IsActive => _isActive;

    public static ProfileSubscription Create(
        ProfileSubscriptionId id,
        SubscribedProfileReference profile,
        SubscriberAddress subscriber,
        SubscriptionChannel channel,
        AlertPolicy alertPolicy,
        DateTimeOffset occurredAtUtc)
    {
        var subscription = new ProfileSubscription();
        subscription.Raise(new ProfileSubscriptionCreated(
            id,
            profile.ProfileId,
            profile.ProfileName,
            subscriber.Value,
            channel.Code,
            alertPolicy.Code,
            alertPolicy.DigestInterval,
            occurredAtUtc));
        return subscription;
    }

    public static ProfileSubscription Rehydrate(IEnumerable<IDomainEvent> history)
    {
        var subscription = new ProfileSubscription();
        subscription.LoadFromHistory(history);
        return subscription;
    }

    public void ChangeAlertPolicy(AlertPolicy alertPolicy, DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(alertPolicy);
        EnsureActive();

        if (_alertPolicy == alertPolicy)
        {
            return;
        }

        Raise(new ProfileSubscriptionAlertPolicyChanged(
            Id,
            alertPolicy.Code,
            alertPolicy.DigestInterval,
            occurredAtUtc));
    }

    public void Deactivate(DateTimeOffset occurredAtUtc)
    {
        if (!_isActive)
        {
            return;
        }

        Raise(new ProfileSubscriptionDeactivated(Id, occurredAtUtc));
    }

    protected override void Apply(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case ProfileSubscriptionCreated created:
                Id = created.SubscriptionId;
                _profile = SubscribedProfileReference.Create(created.ProfileId, created.ProfileName);
                _subscriber = SubscriberAddress.Create(created.Subscriber);
                _channel = created.ChannelCode switch
                {
                    "email" => SubscriptionChannel.Email(),
                    "webhook" => SubscriptionChannel.Webhook(),
                    _ => throw new InvalidOperationException($"Unsupported subscription channel '{created.ChannelCode}'.")
                };
                _alertPolicy = RehydrateAlertPolicy(created.AlertPolicyCode, created.DigestInterval);
                _isActive = true;
                break;
            case ProfileSubscriptionAlertPolicyChanged changed:
                _alertPolicy = RehydrateAlertPolicy(changed.AlertPolicyCode, changed.DigestInterval);
                break;
            case ProfileSubscriptionDeactivated:
                _isActive = false;
                break;
            default:
                throw new InvalidOperationException($"Unsupported domain event type '{domainEvent.GetType().Name}' for profile subscription.");
        }
    }

    private void EnsureActive()
    {
        if (!_isActive)
        {
            throw new InvalidOperationException($"Profile subscription '{Id.Value:D}' is inactive.");
        }
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
