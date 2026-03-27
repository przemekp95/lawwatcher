using LawWatcher.BuildingBlocks.Domain;

namespace LawWatcher.IdentityAndAccess.Domain.OperatorAccounts;

public sealed record OperatorAccountId : ValueObject
{
    public OperatorAccountId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Operator account identifier cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public sealed record OperatorEmail : ValueObject
{
    private OperatorEmail(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static OperatorEmail Create(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0 || !normalized.Contains('@', StringComparison.Ordinal))
        {
            throw new ArgumentException("Operator email must be a non-empty email address.", nameof(value));
        }

        return new OperatorEmail(normalized);
    }

    public override string ToString() => Value;
}

public sealed record OperatorDisplayName : ValueObject
{
    private OperatorDisplayName(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static OperatorDisplayName Create(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Operator display name cannot be empty.", nameof(value));
        }

        return new OperatorDisplayName(normalized);
    }

    public override string ToString() => Value;
}

public sealed record PasswordHash : ValueObject
{
    private PasswordHash(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static PasswordHash FromStored(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Password hash cannot be empty.", nameof(value));
        }

        return new PasswordHash(normalized);
    }

    public override string ToString() => Value;
}

public sealed record OperatorPermission : ValueObject
{
    private OperatorPermission(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static OperatorPermission Of(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Operator permission cannot be empty.", nameof(value));
        }

        return new OperatorPermission(normalized);
    }

    public override string ToString() => Value;
}

public sealed record OperatorAccountRegistered(
    OperatorAccountId OperatorId,
    string Email,
    string DisplayName,
    string PasswordHash,
    IReadOnlyCollection<string> Permissions,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record OperatorAccountUpdated(
    OperatorAccountId OperatorId,
    string DisplayName,
    IReadOnlyCollection<string> Permissions,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record OperatorPasswordReset(
    OperatorAccountId OperatorId,
    string PasswordHash,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record OperatorAccountDeactivated(
    OperatorAccountId OperatorId,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed class OperatorAccount : AggregateRoot<OperatorAccountId>
{
    private readonly List<OperatorPermission> _permissions = [];

    private OperatorEmail _email = OperatorEmail.Create("placeholder@example.test");
    private OperatorDisplayName _displayName = OperatorDisplayName.Create("Placeholder");
    private PasswordHash _passwordHash = PasswordHash.FromStored("pbkdf2$placeholder");

    private OperatorAccount()
    {
    }

    public OperatorEmail Email => _email;

    public OperatorDisplayName DisplayName => _displayName;

    public PasswordHash PasswordHash => _passwordHash;

    public IReadOnlyCollection<OperatorPermission> Permissions => _permissions.AsReadOnly();

    public bool IsActive { get; private set; }

    public DateTimeOffset RegisteredAtUtc { get; private set; }

    public static OperatorAccount Register(
        OperatorAccountId id,
        OperatorEmail email,
        OperatorDisplayName displayName,
        PasswordHash passwordHash,
        IReadOnlyCollection<OperatorPermission> permissions,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var normalizedPermissions = NormalizePermissions(permissions, nameof(permissions));
        var account = new OperatorAccount();
        account.Raise(new OperatorAccountRegistered(
            id,
            email.Value,
            displayName.Value,
            passwordHash.Value,
            normalizedPermissions,
            occurredAtUtc));
        return account;
    }

    public static OperatorAccount Rehydrate(IEnumerable<IDomainEvent> history)
    {
        var account = new OperatorAccount();
        account.LoadFromHistory(history);
        return account;
    }

    public void Update(
        OperatorDisplayName displayName,
        IReadOnlyCollection<OperatorPermission> permissions,
        DateTimeOffset occurredAtUtc)
    {
        var normalizedPermissions = NormalizePermissions(permissions, nameof(permissions));
        if (string.Equals(_displayName.Value, displayName.Value, StringComparison.Ordinal) &&
            normalizedPermissions.SequenceEqual(_permissions.Select(permission => permission.Value), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Raise(new OperatorAccountUpdated(
            Id,
            displayName.Value,
            normalizedPermissions,
            occurredAtUtc));
    }

    public void ResetPassword(PasswordHash passwordHash, DateTimeOffset occurredAtUtc)
    {
        Raise(new OperatorPasswordReset(Id, passwordHash.Value, occurredAtUtc));
    }

    public void Deactivate(DateTimeOffset occurredAtUtc)
    {
        if (!IsActive)
        {
            return;
        }

        Raise(new OperatorAccountDeactivated(Id, occurredAtUtc));
    }

    protected override void Apply(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case OperatorAccountRegistered registered:
                Id = registered.OperatorId;
                _email = OperatorEmail.Create(registered.Email);
                _displayName = OperatorDisplayName.Create(registered.DisplayName);
                _passwordHash = PasswordHash.FromStored(registered.PasswordHash);
                _permissions.Clear();
                _permissions.AddRange(registered.Permissions.Select(OperatorPermission.Of));
                IsActive = true;
                RegisteredAtUtc = registered.OccurredAtUtc;
                break;
            case OperatorAccountUpdated updated:
                _displayName = OperatorDisplayName.Create(updated.DisplayName);
                _permissions.Clear();
                _permissions.AddRange(updated.Permissions.Select(OperatorPermission.Of));
                break;
            case OperatorPasswordReset reset:
                _passwordHash = PasswordHash.FromStored(reset.PasswordHash);
                break;
            case OperatorAccountDeactivated:
                IsActive = false;
                break;
            default:
                throw new InvalidOperationException($"Unsupported domain event type '{domainEvent.GetType().Name}' for operator account.");
        }
    }

    private static string[] NormalizePermissions(IReadOnlyCollection<OperatorPermission> permissions, string paramName)
    {
        var normalizedPermissions = permissions
            .Distinct()
            .OrderBy(permission => permission.Value, StringComparer.OrdinalIgnoreCase)
            .Select(permission => permission.Value)
            .ToArray();

        if (normalizedPermissions.Length == 0)
        {
            throw new ArgumentException("Operator account must have at least one permission.", paramName);
        }

        return normalizedPermissions;
    }
}
