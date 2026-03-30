using LawWatcher.BuildingBlocks.Domain;

namespace LawWatcher.IdentityAndAccess.Domain.ApiClients;

public sealed record ApiClientId : ValueObject
{
    public ApiClientId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "API client identifier cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public sealed record ClientIdentifier : ValueObject
{
    private ClientIdentifier(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ClientIdentifier Create(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Client identifier cannot be empty.", nameof(value));
        }

        return new ClientIdentifier(normalized);
    }

    public override string ToString() => Value;
}

public sealed record TokenFingerprint : ValueObject
{
    private TokenFingerprint(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static TokenFingerprint Create(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Token fingerprint cannot be empty.", nameof(value));
        }

        return new TokenFingerprint(normalized);
    }

    public override string ToString() => Value;
}

public sealed record ApiScope : ValueObject
{
    private ApiScope(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ApiScope Of(string value)
    {
        return new ApiScope(ApiClientScopeCatalog.Normalize(value, nameof(value), "API scope"));
    }

    public override string ToString() => Value;
}

public sealed record ApiClientRegistered(
    ApiClientId ClientId,
    string Name,
    string Identifier,
    string TokenFingerprint,
    IReadOnlyCollection<string> Scopes,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record ApiClientUpdated(
    ApiClientId ClientId,
    string Name,
    string TokenFingerprint,
    IReadOnlyCollection<string> Scopes,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record ApiClientDeactivated(
    ApiClientId ClientId,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed class ApiClient : AggregateRoot<ApiClientId>
{
    private readonly List<ApiScope> _scopes = [];

    private string _name = string.Empty;
    private ClientIdentifier _identifier = ClientIdentifier.Create("placeholder");
    private TokenFingerprint _tokenFingerprint = TokenFingerprint.Create("sha256:placeholder");

    private ApiClient()
    {
    }

    public string Name => _name;

    public ClientIdentifier Identifier => _identifier;

    public TokenFingerprint TokenFingerprint => _tokenFingerprint;

    public IReadOnlyCollection<ApiScope> Scopes => _scopes.AsReadOnly();

    public bool IsActive { get; private set; }

    public DateTimeOffset RegisteredAtUtc { get; private set; }

    public static ApiClient Register(
        ApiClientId id,
        string name,
        ClientIdentifier identifier,
        TokenFingerprint tokenFingerprint,
        IReadOnlyCollection<ApiScope> scopes,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var normalizedScopes = scopes
            .Distinct()
            .OrderBy(scope => scope.Value, StringComparer.OrdinalIgnoreCase)
            .Select(scope => scope.Value)
            .ToArray();

        if (normalizedScopes.Length == 0)
        {
            throw new ArgumentException("API client must have at least one scope.", nameof(scopes));
        }

        var client = new ApiClient();
        client.Raise(new ApiClientRegistered(
            id,
            NormalizeName(name),
            identifier.Value,
            tokenFingerprint.Value,
            normalizedScopes,
            occurredAtUtc));
        return client;
    }

    public static ApiClient Rehydrate(IEnumerable<IDomainEvent> history)
    {
        var client = new ApiClient();
        client.LoadFromHistory(history);
        return client;
    }

    public void Deactivate(DateTimeOffset occurredAtUtc)
    {
        if (!IsActive)
        {
            return;
        }

        Raise(new ApiClientDeactivated(Id, occurredAtUtc));
    }

    public void Update(
        string name,
        TokenFingerprint? tokenFingerprint,
        IReadOnlyCollection<ApiScope> scopes,
        DateTimeOffset occurredAtUtc)
    {
        EnsureActive();

        ArgumentNullException.ThrowIfNull(scopes);

        var normalizedName = NormalizeName(name);
        var normalizedScopes = NormalizeScopes(scopes);
        var effectiveTokenFingerprint = tokenFingerprint?.Value ?? _tokenFingerprint.Value;
        var currentScopes = _scopes
            .Select(scope => scope.Value)
            .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (string.Equals(_name, normalizedName, StringComparison.Ordinal) &&
            string.Equals(_tokenFingerprint.Value, effectiveTokenFingerprint, StringComparison.Ordinal) &&
            currentScopes.SequenceEqual(normalizedScopes, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Raise(new ApiClientUpdated(
            Id,
            normalizedName,
            effectiveTokenFingerprint,
            normalizedScopes,
            occurredAtUtc));
    }

    protected override void Apply(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case ApiClientRegistered registered:
                Id = registered.ClientId;
                _name = registered.Name;
                _identifier = ClientIdentifier.Create(registered.Identifier);
                _tokenFingerprint = TokenFingerprint.Create(registered.TokenFingerprint);
                _scopes.Clear();
                _scopes.AddRange(registered.Scopes.Select(ApiScope.Of));
                IsActive = true;
                RegisteredAtUtc = registered.OccurredAtUtc;
                break;
            case ApiClientUpdated updated:
                _name = updated.Name;
                _tokenFingerprint = TokenFingerprint.Create(updated.TokenFingerprint);
                _scopes.Clear();
                _scopes.AddRange(updated.Scopes.Select(ApiScope.Of));
                IsActive = true;
                break;
            case ApiClientDeactivated:
                IsActive = false;
                break;
            default:
                throw new InvalidOperationException($"Unsupported domain event type '{domainEvent.GetType().Name}' for API client.");
        }
    }

    private static string NormalizeName(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("API client name cannot be empty.", nameof(value));
        }

        return normalized;
    }

    private static string[] NormalizeScopes(IReadOnlyCollection<ApiScope> scopes)
    {
        var normalizedScopes = scopes
            .Distinct()
            .OrderBy(scope => scope.Value, StringComparer.OrdinalIgnoreCase)
            .Select(scope => scope.Value)
            .ToArray();

        if (normalizedScopes.Length == 0)
        {
            throw new ArgumentException("API client must have at least one scope.", nameof(scopes));
        }

        return normalizedScopes;
    }

    private void EnsureActive()
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("API client is inactive.");
        }
    }
}
