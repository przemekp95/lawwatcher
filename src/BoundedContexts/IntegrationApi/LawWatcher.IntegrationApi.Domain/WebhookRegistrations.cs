using LawWatcher.BuildingBlocks.Domain;

namespace LawWatcher.IntegrationApi.Domain.Webhooks;

public sealed record WebhookRegistrationId : ValueObject
{
    public WebhookRegistrationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Webhook registration identifier cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public sealed record WebhookCallbackUrl : ValueObject
{
    private WebhookCallbackUrl(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static WebhookCallbackUrl Create(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Webhook callback URL cannot be empty.", nameof(value));
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out _))
        {
            throw new ArgumentException("Webhook callback URL must be an absolute URI.", nameof(value));
        }

        return new WebhookCallbackUrl(normalized);
    }

    public override string ToString() => Value;
}

public sealed record WebhookEventType : ValueObject
{
    private WebhookEventType(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static WebhookEventType Create(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Webhook event type cannot be empty.", nameof(value));
        }

        return new WebhookEventType(normalized);
    }

    public override string ToString() => Value;
}

public sealed record WebhookRegistered(
    WebhookRegistrationId RegistrationId,
    string Name,
    string CallbackUrl,
    IReadOnlyCollection<string> EventTypes,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record WebhookUpdated(
    WebhookRegistrationId RegistrationId,
    string Name,
    string CallbackUrl,
    IReadOnlyCollection<string> EventTypes,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record WebhookDeactivated(
    WebhookRegistrationId RegistrationId,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed class WebhookRegistration : AggregateRoot<WebhookRegistrationId>
{
    private readonly List<WebhookEventType> _eventTypes = [];

    private string _name = string.Empty;
    private WebhookCallbackUrl _callbackUrl = WebhookCallbackUrl.Create("https://placeholder.example.test/webhook");

    private WebhookRegistration()
    {
    }

    public string Name => _name;

    public WebhookCallbackUrl CallbackUrl => _callbackUrl;

    public IReadOnlyCollection<WebhookEventType> EventTypes => _eventTypes.AsReadOnly();

    public bool IsActive { get; private set; }

    public static WebhookRegistration Register(
        WebhookRegistrationId id,
        string name,
        WebhookCallbackUrl callbackUrl,
        IEnumerable<WebhookEventType> eventTypes,
        DateTimeOffset occurredAtUtc)
    {
        var normalizedEventTypes = NormalizeEventTypes(eventTypes);

        var registration = new WebhookRegistration();
        registration.Raise(new WebhookRegistered(
            id,
            NormalizeName(name),
            callbackUrl.Value,
            normalizedEventTypes,
            occurredAtUtc));
        return registration;
    }

    public static WebhookRegistration Rehydrate(IEnumerable<IDomainEvent> history)
    {
        var registration = new WebhookRegistration();
        registration.LoadFromHistory(history);
        return registration;
    }

    public void Update(
        string name,
        WebhookCallbackUrl callbackUrl,
        IEnumerable<WebhookEventType> eventTypes,
        DateTimeOffset occurredAtUtc)
    {
        EnsureActive();

        var normalizedName = NormalizeName(name);
        var normalizedEventTypes = NormalizeEventTypes(eventTypes);
        var currentEventTypes = _eventTypes
            .Select(eventType => eventType.Value)
            .OrderBy(eventType => eventType, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (string.Equals(_name, normalizedName, StringComparison.Ordinal) &&
            string.Equals(_callbackUrl.Value, callbackUrl.Value, StringComparison.Ordinal) &&
            currentEventTypes.SequenceEqual(normalizedEventTypes, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Raise(new WebhookUpdated(
            Id,
            normalizedName,
            callbackUrl.Value,
            normalizedEventTypes,
            occurredAtUtc));
    }

    public void Deactivate(DateTimeOffset occurredAtUtc)
    {
        if (!IsActive)
        {
            return;
        }

        Raise(new WebhookDeactivated(Id, occurredAtUtc));
    }

    protected override void Apply(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case WebhookRegistered registered:
                Id = registered.RegistrationId;
                _name = registered.Name;
                _callbackUrl = WebhookCallbackUrl.Create(registered.CallbackUrl);
                _eventTypes.Clear();
                _eventTypes.AddRange(registered.EventTypes.Select(WebhookEventType.Create));
                IsActive = true;
                break;
            case WebhookUpdated updated:
                _name = updated.Name;
                _callbackUrl = WebhookCallbackUrl.Create(updated.CallbackUrl);
                _eventTypes.Clear();
                _eventTypes.AddRange(updated.EventTypes.Select(WebhookEventType.Create));
                IsActive = true;
                break;
            case WebhookDeactivated:
                IsActive = false;
                break;
            default:
                throw new InvalidOperationException($"Unsupported domain event type '{domainEvent.GetType().Name}' for webhook registration.");
        }
    }

    private static string NormalizeName(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Webhook registration name cannot be empty.", nameof(value));
        }

        return normalized;
    }

    private static string[] NormalizeEventTypes(IEnumerable<WebhookEventType> eventTypes)
    {
        var normalizedEventTypes = eventTypes
            .Distinct()
            .OrderBy(eventType => eventType.Value, StringComparer.OrdinalIgnoreCase)
            .Select(eventType => eventType.Value)
            .ToArray();

        if (normalizedEventTypes.Length == 0)
        {
            throw new InvalidOperationException("Webhook registration requires at least one event type.");
        }

        return normalizedEventTypes;
    }

    private void EnsureActive()
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("Webhook registration is inactive.");
        }
    }
}
