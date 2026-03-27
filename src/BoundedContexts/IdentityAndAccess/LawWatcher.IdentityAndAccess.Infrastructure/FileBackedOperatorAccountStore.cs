using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.IdentityAndAccess.Application;
using LawWatcher.IdentityAndAccess.Domain.OperatorAccounts;

namespace LawWatcher.IdentityAndAccess.Infrastructure;

public sealed class FileBackedOperatorAccountRepository(string rootPath) : IOperatorAccountRepository
{
    private readonly string _rootPath = rootPath;
    private readonly string _streamsDirectory = Path.Combine(rootPath, "streams");

    public Task<OperatorAccount?> GetAsync(OperatorAccountId id, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    GetStreamPath(id),
                    () => new OperatorAccountStreamDocument([]),
                    ct);

                return document.Events.Length == 0
                    ? null
                    : OperatorAccount.Rehydrate(document.Events.Select(ToDomainEvent).ToArray());
            },
            cancellationToken);
    }

    public Task SaveAsync(OperatorAccount account, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var pendingEvents = account.UncommittedEvents.ToArray();
                if (pendingEvents.Length == 0)
                {
                    return;
                }

                var streamPath = GetStreamPath(account.Id);
                var document = await JsonFilePersistence.LoadAsync(
                    streamPath,
                    () => new OperatorAccountStreamDocument([]),
                    ct);

                var expectedVersion = account.Version - pendingEvents.Length;
                if (document.Events.Length != expectedVersion)
                {
                    throw new InvalidOperationException($"Optimistic concurrency violation for operator account stream '{account.Id.Value:D}'.");
                }

                await JsonFilePersistence.SaveAsync(
                    streamPath,
                    new OperatorAccountStreamDocument(document.Events.Concat(pendingEvents.Select(FromDomainEvent)).ToArray()),
                    ct);

                account.DequeueUncommittedEvents();
            },
            cancellationToken);
    }

    private string GetStreamPath(OperatorAccountId id) => Path.Combine(_streamsDirectory, $"{id.Value:D}.json");

    private static OperatorAccountEventRecord FromDomainEvent(IDomainEvent domainEvent) =>
        domainEvent switch
        {
            OperatorAccountRegistered registered => new OperatorAccountEventRecord(
                "registered",
                registered.OperatorId.Value,
                registered.Email,
                registered.DisplayName,
                registered.PasswordHash,
                registered.Permissions.ToArray(),
                registered.OccurredAtUtc),
            OperatorAccountUpdated updated => new OperatorAccountEventRecord(
                "updated",
                updated.OperatorId.Value,
                null,
                updated.DisplayName,
                null,
                updated.Permissions.ToArray(),
                updated.OccurredAtUtc),
            OperatorPasswordReset reset => new OperatorAccountEventRecord(
                "password-reset",
                reset.OperatorId.Value,
                null,
                null,
                reset.PasswordHash,
                null,
                reset.OccurredAtUtc),
            OperatorAccountDeactivated deactivated => new OperatorAccountEventRecord(
                "deactivated",
                deactivated.OperatorId.Value,
                null,
                null,
                null,
                null,
                deactivated.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported operator account domain event type '{domainEvent.GetType().Name}'.")
        };

    private static IDomainEvent ToDomainEvent(OperatorAccountEventRecord record) =>
        record.Type switch
        {
            "registered" => new OperatorAccountRegistered(
                new OperatorAccountId(record.OperatorId),
                record.Email ?? throw new InvalidOperationException("Operator account registered event is missing email."),
                record.DisplayName ?? throw new InvalidOperationException("Operator account registered event is missing display name."),
                record.PasswordHash ?? throw new InvalidOperationException("Operator account registered event is missing password hash."),
                record.Permissions ?? throw new InvalidOperationException("Operator account registered event is missing permissions."),
                record.OccurredAtUtc),
            "updated" => new OperatorAccountUpdated(
                new OperatorAccountId(record.OperatorId),
                record.DisplayName ?? throw new InvalidOperationException("Operator account updated event is missing display name."),
                record.Permissions ?? throw new InvalidOperationException("Operator account updated event is missing permissions."),
                record.OccurredAtUtc),
            "password-reset" => new OperatorPasswordReset(
                new OperatorAccountId(record.OperatorId),
                record.PasswordHash ?? throw new InvalidOperationException("Operator password reset event is missing password hash."),
                record.OccurredAtUtc),
            "deactivated" => new OperatorAccountDeactivated(
                new OperatorAccountId(record.OperatorId),
                record.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported operator account event record type '{record.Type}'.")
        };

    private sealed record OperatorAccountStreamDocument(OperatorAccountEventRecord[] Events);

    private sealed record OperatorAccountEventRecord(
        string Type,
        Guid OperatorId,
        string? Email,
        string? DisplayName,
        string? PasswordHash,
        string[]? Permissions,
        DateTimeOffset OccurredAtUtc);
}

public sealed class FileBackedOperatorAccountProjectionStore(string rootPath) : IOperatorAccountReadRepository, IOperatorAccountProjection
{
    private readonly string _rootPath = rootPath;
    private readonly string _projectionPath = Path.Combine(rootPath, "projection.json");

    public Task<IReadOnlyCollection<OperatorAccountReadModel>> GetOperatorsAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new OperatorAccountProjectionDocument([]),
                    ct);

                return (IReadOnlyCollection<OperatorAccountReadModel>)document.Operators
                    .OrderBy(record => record.Email, StringComparer.OrdinalIgnoreCase)
                    .Select(ToReadModel)
                    .ToArray();
            },
            cancellationToken);
    }

    public async Task<OperatorAccountReadModel?> GetByIdAsync(Guid operatorId, CancellationToken cancellationToken)
    {
        var operators = await GetOperatorsAsync(cancellationToken);
        return operators.SingleOrDefault(@operator => @operator.Id == operatorId);
    }

    public async Task<OperatorAccountReadModel?> GetByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var operators = await GetOperatorsAsync(cancellationToken);
        return operators.SingleOrDefault(@operator =>
            @operator.Email.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase));
    }

    public Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new OperatorAccountProjectionDocument([]),
                    ct);

                var operators = document.Operators.ToDictionary(record => record.Id);
                foreach (var domainEvent in domainEvents)
                {
                    switch (domainEvent)
                    {
                        case OperatorAccountRegistered registered:
                            operators[registered.OperatorId.Value] = new OperatorAccountProjectionRecord(
                                registered.OperatorId.Value,
                                registered.Email,
                                registered.DisplayName,
                                registered.PasswordHash,
                                registered.Permissions
                                    .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
                                    .ToArray(),
                                true,
                                registered.OccurredAtUtc);
                            break;
                        case OperatorAccountUpdated updated when operators.TryGetValue(updated.OperatorId.Value, out var existing):
                            operators[updated.OperatorId.Value] = existing with
                            {
                                DisplayName = updated.DisplayName,
                                Permissions = updated.Permissions
                                    .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
                                    .ToArray()
                            };
                            break;
                        case OperatorPasswordReset reset when operators.TryGetValue(reset.OperatorId.Value, out var resetExisting):
                            operators[reset.OperatorId.Value] = resetExisting with
                            {
                                PasswordHash = reset.PasswordHash
                            };
                            break;
                        case OperatorAccountDeactivated deactivated when operators.TryGetValue(deactivated.OperatorId.Value, out var deactivatedExisting):
                            operators[deactivated.OperatorId.Value] = deactivatedExisting with
                            {
                                IsActive = false
                            };
                            break;
                    }
                }

                await JsonFilePersistence.SaveAsync(
                    _projectionPath,
                    new OperatorAccountProjectionDocument(
                        operators.Values
                            .OrderBy(@operator => @operator.Email, StringComparer.OrdinalIgnoreCase)
                            .ToArray()),
                    ct);
            },
            cancellationToken);
    }

    private static OperatorAccountReadModel ToReadModel(OperatorAccountProjectionRecord record)
    {
        return new OperatorAccountReadModel(
            record.Id,
            record.Email,
            record.DisplayName,
            record.PasswordHash,
            record.Permissions,
            record.IsActive,
            record.RegisteredAtUtc);
    }

    private sealed record OperatorAccountProjectionDocument(OperatorAccountProjectionRecord[] Operators);

    private sealed record OperatorAccountProjectionRecord(
        Guid Id,
        string Email,
        string DisplayName,
        string PasswordHash,
        string[] Permissions,
        bool IsActive,
        DateTimeOffset RegisteredAtUtc);
}
