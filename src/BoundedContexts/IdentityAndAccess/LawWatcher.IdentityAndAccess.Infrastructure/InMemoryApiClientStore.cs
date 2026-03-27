using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.IdentityAndAccess.Application;
using LawWatcher.IdentityAndAccess.Domain.ApiClients;

namespace LawWatcher.IdentityAndAccess.Infrastructure;

public sealed class InMemoryApiClientRepository : IApiClientRepository
{
    private readonly Dictionary<string, List<IDomainEvent>> _streams = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<ApiClient?> GetAsync(ApiClientId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_streams.TryGetValue(GetStreamId(id), out var history))
            {
                return Task.FromResult<ApiClient?>(null);
            }

            return Task.FromResult<ApiClient?>(ApiClient.Rehydrate(history.ToArray()));
        }
    }

    public Task SaveAsync(ApiClient client, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pendingEvents = client.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return Task.CompletedTask;
        }

        var streamId = GetStreamId(client.Id);
        var expectedVersion = client.Version - pendingEvents.Length;

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

        client.DequeueUncommittedEvents();
        return Task.CompletedTask;
    }

    private static string GetStreamId(ApiClientId id) => $"identity-api-client-{id.Value:D}";
}

public sealed class InMemoryApiClientProjectionStore : IApiClientReadRepository, IApiClientProjection
{
    private readonly Dictionary<Guid, ProjectionState> _clients = new();
    private readonly Lock _gate = new();

    public Task<IReadOnlyCollection<ApiClientReadModel>> GetApiClientsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyCollection<ApiClientReadModel>>(
                _clients.Values
                    .Select(state => state.ToReadModel())
                    .ToArray());
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
                    case ApiClientRegistered registered:
                        _clients[registered.ClientId.Value] = ProjectionState.From(registered);
                        break;
                    case ApiClientUpdated updated when _clients.TryGetValue(updated.ClientId.Value, out var updatedExisting):
                        updatedExisting.Update(
                            updated.Name,
                            updated.TokenFingerprint,
                            updated.Scopes.OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase).ToArray());
                        break;
                    case ApiClientDeactivated deactivated when _clients.TryGetValue(deactivated.ClientId.Value, out var deactivatedExisting):
                        deactivatedExisting.Deactivate();
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
            string name,
            string clientIdentifier,
            string tokenFingerprint,
            IReadOnlyCollection<string> scopes,
            DateTimeOffset registeredAtUtc)
        {
            Id = id;
            Name = name;
            ClientIdentifier = clientIdentifier;
            TokenFingerprint = tokenFingerprint;
            Scopes = scopes;
            RegisteredAtUtc = registeredAtUtc;
            IsActive = true;
        }

        public Guid Id { get; }

        public string Name { get; private set; }

        public string ClientIdentifier { get; }

        public string TokenFingerprint { get; private set; }

        public IReadOnlyCollection<string> Scopes { get; private set; }

        public bool IsActive { get; private set; }

        public DateTimeOffset RegisteredAtUtc { get; }

        public static ProjectionState From(ApiClientRegistered registered)
        {
            return new ProjectionState(
                registered.ClientId.Value,
                registered.Name,
                registered.Identifier,
                registered.TokenFingerprint,
                registered.Scopes.OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase).ToArray(),
                registered.OccurredAtUtc);
        }

        public void Deactivate()
        {
            IsActive = false;
        }

        public void Update(string name, string tokenFingerprint, IReadOnlyCollection<string> scopes)
        {
            Name = name;
            TokenFingerprint = tokenFingerprint;
            Scopes = scopes;
            IsActive = true;
        }

        public ApiClientReadModel ToReadModel()
        {
            return new ApiClientReadModel(
                Id,
                Name,
                ClientIdentifier,
                TokenFingerprint,
                Scopes,
                IsActive,
                RegisteredAtUtc);
        }
    }
}
