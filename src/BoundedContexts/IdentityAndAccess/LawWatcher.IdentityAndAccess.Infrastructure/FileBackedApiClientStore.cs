using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.IdentityAndAccess.Application;
using LawWatcher.IdentityAndAccess.Domain.ApiClients;

namespace LawWatcher.IdentityAndAccess.Infrastructure;

public sealed class FileBackedApiClientRepository(string rootPath) : IApiClientRepository
{
    private readonly string _rootPath = rootPath;
    private readonly string _streamsDirectory = Path.Combine(rootPath, "streams");

    public Task<ApiClient?> GetAsync(ApiClientId id, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    GetStreamPath(id),
                    () => new ApiClientStreamDocument([]),
                    ct);

                return document.Events.Length == 0
                    ? null
                    : ApiClient.Rehydrate(document.Events.Select(ToDomainEvent).ToArray());
            },
            cancellationToken);
    }

    public Task SaveAsync(ApiClient client, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var pendingEvents = client.UncommittedEvents.ToArray();
                if (pendingEvents.Length == 0)
                {
                    return;
                }

                var streamPath = GetStreamPath(client.Id);
                var document = await JsonFilePersistence.LoadAsync(
                    streamPath,
                    () => new ApiClientStreamDocument([]),
                    ct);

                var expectedVersion = client.Version - pendingEvents.Length;
                if (document.Events.Length != expectedVersion)
                {
                    throw new InvalidOperationException($"Optimistic concurrency violation for API client stream '{client.Id.Value:D}'.");
                }

                await JsonFilePersistence.SaveAsync(
                    streamPath,
                    new ApiClientStreamDocument(document.Events.Concat(pendingEvents.Select(FromDomainEvent)).ToArray()),
                    ct);

                client.DequeueUncommittedEvents();
            },
            cancellationToken);
    }

    private string GetStreamPath(ApiClientId id) => Path.Combine(_streamsDirectory, $"{id.Value:D}.json");

    private static ApiClientEventRecord FromDomainEvent(IDomainEvent domainEvent) =>
        domainEvent switch
        {
            ApiClientRegistered registered => new ApiClientEventRecord(
                "registered",
                registered.ClientId.Value,
                registered.Name,
                registered.Identifier,
                registered.TokenFingerprint,
                registered.Scopes.ToArray(),
                registered.OccurredAtUtc),
            ApiClientUpdated updated => new ApiClientEventRecord(
                "updated",
                updated.ClientId.Value,
                updated.Name,
                null,
                updated.TokenFingerprint,
                updated.Scopes.ToArray(),
                updated.OccurredAtUtc),
            ApiClientDeactivated deactivated => new ApiClientEventRecord(
                "deactivated",
                deactivated.ClientId.Value,
                null,
                null,
                null,
                null,
                deactivated.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported API client domain event type '{domainEvent.GetType().Name}'.")
        };

    private static IDomainEvent ToDomainEvent(ApiClientEventRecord record) =>
        record.Type switch
        {
            "registered" => new ApiClientRegistered(
                new ApiClientId(record.ClientId),
                record.Name ?? throw new InvalidOperationException("API client registered event is missing name."),
                record.ClientIdentifier ?? throw new InvalidOperationException("API client registered event is missing client identifier."),
                record.TokenFingerprint ?? throw new InvalidOperationException("API client registered event is missing token fingerprint."),
                record.Scopes ?? throw new InvalidOperationException("API client registered event is missing scopes."),
                record.OccurredAtUtc),
            "updated" => new ApiClientUpdated(
                new ApiClientId(record.ClientId),
                record.Name ?? throw new InvalidOperationException("API client updated event is missing name."),
                record.TokenFingerprint ?? throw new InvalidOperationException("API client updated event is missing token fingerprint."),
                record.Scopes ?? throw new InvalidOperationException("API client updated event is missing scopes."),
                record.OccurredAtUtc),
            "deactivated" => new ApiClientDeactivated(
                new ApiClientId(record.ClientId),
                record.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported API client event record type '{record.Type}'.")
        };

    private sealed record ApiClientStreamDocument(ApiClientEventRecord[] Events);

    private sealed record ApiClientEventRecord(
        string Type,
        Guid ClientId,
        string? Name,
        string? ClientIdentifier,
        string? TokenFingerprint,
        string[]? Scopes,
        DateTimeOffset OccurredAtUtc);
}

public sealed class FileBackedApiClientProjectionStore(string rootPath) : IApiClientReadRepository, IApiClientProjection
{
    private readonly string _rootPath = rootPath;
    private readonly string _projectionPath = Path.Combine(rootPath, "projection.json");

    public Task<IReadOnlyCollection<ApiClientReadModel>> GetApiClientsAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new ApiClientProjectionDocument([]),
                    ct);

                return (IReadOnlyCollection<ApiClientReadModel>)document.Clients
                    .Select(record => new ApiClientReadModel(
                        record.Id,
                        record.Name,
                        record.ClientIdentifier,
                        record.TokenFingerprint,
                        record.Scopes,
                        record.IsActive,
                        record.RegisteredAtUtc))
                    .ToArray();
            },
            cancellationToken);
    }

    public Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new ApiClientProjectionDocument([]),
                    ct);

                var clients = document.Clients.ToDictionary(record => record.Id);
                foreach (var domainEvent in domainEvents)
                {
                    switch (domainEvent)
                    {
                        case ApiClientRegistered registered:
                            clients[registered.ClientId.Value] = new ApiClientProjectionRecord(
                                registered.ClientId.Value,
                                registered.Name,
                                registered.Identifier,
                                registered.TokenFingerprint,
                                registered.Scopes
                                    .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
                                    .ToArray(),
                                true,
                                registered.OccurredAtUtc);
                            break;
                        case ApiClientUpdated updated when clients.TryGetValue(updated.ClientId.Value, out var updatedExisting):
                            clients[updated.ClientId.Value] = updatedExisting with
                            {
                                Name = updated.Name,
                                TokenFingerprint = updated.TokenFingerprint,
                                Scopes = updated.Scopes
                                    .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
                                    .ToArray(),
                                IsActive = true
                            };
                            break;
                        case ApiClientDeactivated deactivated when clients.TryGetValue(deactivated.ClientId.Value, out var existing):
                            clients[deactivated.ClientId.Value] = existing with { IsActive = false };
                            break;
                    }
                }

                await JsonFilePersistence.SaveAsync(
                    _projectionPath,
                    new ApiClientProjectionDocument(
                        clients.Values
                            .OrderBy(client => client.Name, StringComparer.OrdinalIgnoreCase)
                            .ToArray()),
                    ct);
            },
            cancellationToken);
    }

    private sealed record ApiClientProjectionDocument(ApiClientProjectionRecord[] Clients);

    private sealed record ApiClientProjectionRecord(
        Guid Id,
        string Name,
        string ClientIdentifier,
        string TokenFingerprint,
        string[] Scopes,
        bool IsActive,
        DateTimeOffset RegisteredAtUtc);
}
