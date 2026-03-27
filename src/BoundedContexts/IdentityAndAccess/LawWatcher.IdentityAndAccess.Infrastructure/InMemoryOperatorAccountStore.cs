using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.IdentityAndAccess.Application;
using LawWatcher.IdentityAndAccess.Domain.OperatorAccounts;

namespace LawWatcher.IdentityAndAccess.Infrastructure;

public sealed class InMemoryOperatorAccountRepository : IOperatorAccountRepository
{
    private readonly Dictionary<string, List<IDomainEvent>> _streams = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<OperatorAccount?> GetAsync(OperatorAccountId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_streams.TryGetValue(GetStreamId(id), out var history))
            {
                return Task.FromResult<OperatorAccount?>(null);
            }

            return Task.FromResult<OperatorAccount?>(OperatorAccount.Rehydrate(history.ToArray()));
        }
    }

    public Task SaveAsync(OperatorAccount account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pendingEvents = account.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return Task.CompletedTask;
        }

        var streamId = GetStreamId(account.Id);
        var expectedVersion = account.Version - pendingEvents.Length;

        lock (_gate)
        {
            if (!_streams.TryGetValue(streamId, out var history))
            {
                history = [];
                _streams.Add(streamId, history);
            }

            if (history.Count != expectedVersion)
            {
                throw new InvalidOperationException($"Optimistic concurrency violation for stream '{streamId}'.");
            }

            history.AddRange(pendingEvents);
        }

        account.DequeueUncommittedEvents();
        return Task.CompletedTask;
    }

    private static string GetStreamId(OperatorAccountId id) => $"identity-operator-account-{id.Value:D}";
}

public sealed class InMemoryOperatorAccountProjectionStore : IOperatorAccountReadRepository, IOperatorAccountProjection
{
    private readonly Dictionary<Guid, ProjectionState> _operators = new();
    private readonly Lock _gate = new();

    public Task<IReadOnlyCollection<OperatorAccountReadModel>> GetOperatorsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyCollection<OperatorAccountReadModel>>(
                _operators.Values
                    .OrderBy(state => state.Email, StringComparer.OrdinalIgnoreCase)
                    .Select(state => state.ToReadModel())
                    .ToArray());
        }
    }

    public Task<OperatorAccountReadModel?> GetByIdAsync(Guid operatorId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<OperatorAccountReadModel?>(
                _operators.TryGetValue(operatorId, out var state)
                    ? state.ToReadModel()
                    : null);
        }
    }

    public Task<OperatorAccountReadModel?> GetByEmailAsync(string email, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var normalizedEmail = email.Trim().ToLowerInvariant();
            var match = _operators.Values.SingleOrDefault(state =>
                state.Email.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult<OperatorAccountReadModel?>(match?.ToReadModel());
        }
    }

    public Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            foreach (var domainEvent in domainEvents)
            {
                switch (domainEvent)
                {
                    case OperatorAccountRegistered registered:
                        _operators[registered.OperatorId.Value] = ProjectionState.From(registered);
                        break;
                    case OperatorAccountUpdated updated when _operators.TryGetValue(updated.OperatorId.Value, out var updatedState):
                        updatedState.Update(updated.DisplayName, updated.Permissions);
                        break;
                    case OperatorPasswordReset reset when _operators.TryGetValue(reset.OperatorId.Value, out var resetState):
                        resetState.ResetPassword(reset.PasswordHash);
                        break;
                    case OperatorAccountDeactivated deactivated when _operators.TryGetValue(deactivated.OperatorId.Value, out var deactivatedState):
                        deactivatedState.Deactivate();
                        break;
                }
            }
        }

        return Task.CompletedTask;
    }

    private sealed class ProjectionState
    {
        private ProjectionState(
            Guid id,
            string email,
            string displayName,
            string passwordHash,
            IReadOnlyCollection<string> permissions,
            DateTimeOffset registeredAtUtc)
        {
            Id = id;
            Email = email;
            DisplayName = displayName;
            PasswordHash = passwordHash;
            Permissions = permissions;
            RegisteredAtUtc = registeredAtUtc;
            IsActive = true;
        }

        public Guid Id { get; }

        public string Email { get; }

        public string DisplayName { get; private set; }

        public string PasswordHash { get; private set; }

        public IReadOnlyCollection<string> Permissions { get; private set; }

        public bool IsActive { get; private set; }

        public DateTimeOffset RegisteredAtUtc { get; }

        public static ProjectionState From(OperatorAccountRegistered registered)
        {
            return new ProjectionState(
                registered.OperatorId.Value,
                registered.Email,
                registered.DisplayName,
                registered.PasswordHash,
                registered.Permissions.OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase).ToArray(),
                registered.OccurredAtUtc);
        }

        public void Update(string displayName, IReadOnlyCollection<string> permissions)
        {
            DisplayName = displayName;
            Permissions = permissions.OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public void ResetPassword(string passwordHash)
        {
            PasswordHash = passwordHash;
        }

        public void Deactivate()
        {
            IsActive = false;
        }

        public OperatorAccountReadModel ToReadModel()
        {
            return new OperatorAccountReadModel(
                Id,
                Email,
                DisplayName,
                PasswordHash,
                Permissions,
                IsActive,
                RegisteredAtUtc);
        }
    }
}
