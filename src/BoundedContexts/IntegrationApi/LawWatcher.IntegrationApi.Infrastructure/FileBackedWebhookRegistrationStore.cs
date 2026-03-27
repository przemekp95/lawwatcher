using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Domain.Webhooks;

namespace LawWatcher.IntegrationApi.Infrastructure;

public sealed class FileBackedWebhookRegistrationRepository(string rootPath) : IWebhookRegistrationRepository
{
    private readonly string _rootPath = rootPath;
    private readonly string _streamsDirectory = Path.Combine(rootPath, "streams");

    public Task<WebhookRegistration?> GetAsync(WebhookRegistrationId id, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    GetStreamPath(id),
                    () => new WebhookRegistrationStreamDocument([]),
                    ct);

                return document.Events.Length == 0
                    ? null
                    : WebhookRegistration.Rehydrate(document.Events.Select(ToDomainEvent).ToArray());
            },
            cancellationToken);
    }

    public Task SaveAsync(WebhookRegistration registration, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var pendingEvents = registration.UncommittedEvents.ToArray();
                if (pendingEvents.Length == 0)
                {
                    return;
                }

                var streamPath = GetStreamPath(registration.Id);
                var document = await JsonFilePersistence.LoadAsync(
                    streamPath,
                    () => new WebhookRegistrationStreamDocument([]),
                    ct);

                var expectedVersion = registration.Version - pendingEvents.Length;
                if (document.Events.Length != expectedVersion)
                {
                    throw new InvalidOperationException($"Optimistic concurrency violation for webhook registration stream '{registration.Id.Value:D}'.");
                }

                await JsonFilePersistence.SaveAsync(
                    streamPath,
                    new WebhookRegistrationStreamDocument(document.Events.Concat(pendingEvents.Select(FromDomainEvent)).ToArray()),
                    ct);

                registration.DequeueUncommittedEvents();
            },
            cancellationToken);
    }

    private string GetStreamPath(WebhookRegistrationId id) => Path.Combine(_streamsDirectory, $"{id.Value:D}.json");

    private static WebhookRegistrationEventRecord FromDomainEvent(IDomainEvent domainEvent) =>
        domainEvent switch
        {
            WebhookRegistered registered => new WebhookRegistrationEventRecord(
                "registered",
                registered.RegistrationId.Value,
                registered.Name,
                registered.CallbackUrl,
                registered.EventTypes.ToArray(),
                registered.OccurredAtUtc),
            WebhookUpdated updated => new WebhookRegistrationEventRecord(
                "updated",
                updated.RegistrationId.Value,
                updated.Name,
                updated.CallbackUrl,
                updated.EventTypes.ToArray(),
                updated.OccurredAtUtc),
            WebhookDeactivated deactivated => new WebhookRegistrationEventRecord(
                "deactivated",
                deactivated.RegistrationId.Value,
                null,
                null,
                [],
                deactivated.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported webhook registration domain event type '{domainEvent.GetType().Name}'.")
        };

    private static IDomainEvent ToDomainEvent(WebhookRegistrationEventRecord record) =>
        record.Type switch
        {
            "registered" => new WebhookRegistered(
                new WebhookRegistrationId(record.RegistrationId),
                record.Name ?? throw new InvalidOperationException("Webhook registered event is missing name."),
                record.CallbackUrl ?? throw new InvalidOperationException("Webhook registered event is missing callback URL."),
                record.EventTypes,
                record.OccurredAtUtc),
            "updated" => new WebhookUpdated(
                new WebhookRegistrationId(record.RegistrationId),
                record.Name ?? throw new InvalidOperationException("Webhook updated event is missing name."),
                record.CallbackUrl ?? throw new InvalidOperationException("Webhook updated event is missing callback URL."),
                record.EventTypes,
                record.OccurredAtUtc),
            "deactivated" => new WebhookDeactivated(new WebhookRegistrationId(record.RegistrationId), record.OccurredAtUtc),
            _ => throw new InvalidOperationException($"Unsupported webhook registration event record type '{record.Type}'.")
        };

    private sealed record WebhookRegistrationStreamDocument(WebhookRegistrationEventRecord[] Events);

    private sealed record WebhookRegistrationEventRecord(
        string Type,
        Guid RegistrationId,
        string? Name,
        string? CallbackUrl,
        string[] EventTypes,
        DateTimeOffset OccurredAtUtc);
}

public sealed class FileBackedWebhookRegistrationProjectionStore(string rootPath) : IWebhookRegistrationReadRepository, IWebhookRegistrationProjection
{
    private readonly string _rootPath = rootPath;
    private readonly string _projectionPath = Path.Combine(rootPath, "projection.json");

    public Task<IReadOnlyCollection<WebhookRegistrationReadModel>> GetWebhooksAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new WebhookRegistrationProjectionDocument([]),
                    ct);

                return (IReadOnlyCollection<WebhookRegistrationReadModel>)document.Webhooks.ToArray();
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
                    () => new WebhookRegistrationProjectionDocument([]),
                    ct);

                var webhooks = document.Webhooks.ToDictionary(webhook => webhook.Id);
                foreach (var domainEvent in domainEvents)
                {
                    switch (domainEvent)
                    {
                        case WebhookRegistered registered:
                            webhooks[registered.RegistrationId.Value] = new WebhookRegistrationReadModel(
                                registered.RegistrationId.Value,
                                registered.Name,
                                registered.CallbackUrl,
                                registered.EventTypes.OrderBy(eventType => eventType, StringComparer.OrdinalIgnoreCase).ToArray(),
                                true);
                            break;
                        case WebhookUpdated updated:
                            webhooks[updated.RegistrationId.Value] = new WebhookRegistrationReadModel(
                                updated.RegistrationId.Value,
                                updated.Name,
                                updated.CallbackUrl,
                                updated.EventTypes.OrderBy(eventType => eventType, StringComparer.OrdinalIgnoreCase).ToArray(),
                                true);
                            break;
                        case WebhookDeactivated deactivated when webhooks.TryGetValue(deactivated.RegistrationId.Value, out var existing):
                            webhooks[deactivated.RegistrationId.Value] = existing with { IsActive = false };
                            break;
                    }
                }

                await JsonFilePersistence.SaveAsync(
                    _projectionPath,
                    new WebhookRegistrationProjectionDocument(webhooks.Values.ToArray()),
                    ct);
            },
            cancellationToken);
    }

    private sealed record WebhookRegistrationProjectionDocument(WebhookRegistrationReadModel[] Webhooks);
}
