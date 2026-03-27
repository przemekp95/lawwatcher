using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Domain.Webhooks;

namespace LawWatcher.IntegrationApi.Infrastructure;

public sealed class InMemoryWebhookRegistrationRepository : IWebhookRegistrationRepository
{
    private readonly Dictionary<string, List<IDomainEvent>> _streams = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<WebhookRegistration?> GetAsync(WebhookRegistrationId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_streams.TryGetValue(GetStreamId(id), out var history))
            {
                return Task.FromResult<WebhookRegistration?>(null);
            }

            return Task.FromResult<WebhookRegistration?>(WebhookRegistration.Rehydrate(history.ToArray()));
        }
    }

    public Task SaveAsync(WebhookRegistration registration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pendingEvents = registration.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return Task.CompletedTask;
        }

        var streamId = GetStreamId(registration.Id);
        var expectedVersion = registration.Version - pendingEvents.Length;

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

        registration.DequeueUncommittedEvents();
        return Task.CompletedTask;
    }

    private static string GetStreamId(WebhookRegistrationId id) => $"integration-api-webhook-{id.Value:D}";
}

public sealed class InMemoryWebhookRegistrationProjectionStore : IWebhookRegistrationReadRepository, IWebhookRegistrationProjection
{
    private readonly Dictionary<Guid, ProjectionState> _webhooks = new();
    private readonly Lock _gate = new();

    public Task<IReadOnlyCollection<WebhookRegistrationReadModel>> GetWebhooksAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyCollection<WebhookRegistrationReadModel>>(
                _webhooks.Values
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
                    case WebhookRegistered registered:
                        _webhooks[registered.RegistrationId.Value] = ProjectionState.From(registered);
                        break;
                    case WebhookUpdated updated when _webhooks.TryGetValue(updated.RegistrationId.Value, out var updatedExisting):
                        updatedExisting.Update(
                            updated.Name,
                            updated.CallbackUrl,
                            updated.EventTypes
                                .OrderBy(eventType => eventType, StringComparer.OrdinalIgnoreCase)
                                .ToArray());
                        break;
                    case WebhookDeactivated deactivated when _webhooks.TryGetValue(deactivated.RegistrationId.Value, out var deactivatedExisting):
                        deactivatedExisting.Deactivate();
                        break;
                }
            }
        }

        return Task.CompletedTask;
    }

    private sealed class ProjectionState
    {
        private ProjectionState(Guid id, string name, string callbackUrl, IReadOnlyCollection<string> eventTypes)
        {
            Id = id;
            Name = name;
            CallbackUrl = callbackUrl;
            EventTypes = eventTypes;
            IsActive = true;
        }

        public Guid Id { get; }

        public string Name { get; private set; }

        public string CallbackUrl { get; private set; }

        public IReadOnlyCollection<string> EventTypes { get; private set; }

        public bool IsActive { get; private set; }

        public static ProjectionState From(WebhookRegistered registered)
        {
            return new ProjectionState(
                registered.RegistrationId.Value,
                registered.Name,
                registered.CallbackUrl,
                registered.EventTypes
                    .OrderBy(eventType => eventType, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        }

        public void Deactivate()
        {
            IsActive = false;
        }

        public void Update(string name, string callbackUrl, IReadOnlyCollection<string> eventTypes)
        {
            Name = name;
            CallbackUrl = callbackUrl;
            EventTypes = eventTypes;
            IsActive = true;
        }

        public WebhookRegistrationReadModel ToReadModel()
        {
            return new WebhookRegistrationReadModel(
                Id,
                Name,
                CallbackUrl,
                EventTypes,
                IsActive);
        }
    }
}
