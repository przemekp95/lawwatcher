using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.BuildingBlocks.Ports;
using LawWatcher.IntegrationApi.Contracts;
using LawWatcher.IntegrationApi.Domain.Webhooks;
using System.Text.Json;

namespace LawWatcher.IntegrationApi.Application;

public sealed record RegisterWebhookCommand(
    Guid RegistrationId,
    string Name,
    string CallbackUrl,
    IReadOnlyCollection<string> EventTypes) : Command;

public sealed record DeactivateWebhookCommand(
    Guid RegistrationId) : Command;

public sealed record UpdateWebhookCommand(
    Guid RegistrationId,
    string Name,
    string CallbackUrl,
    IReadOnlyCollection<string> EventTypes) : Command;

public sealed record WebhookRegistrationReadModel(
    Guid Id,
    string Name,
    string CallbackUrl,
    IReadOnlyCollection<string> EventTypes,
    bool IsActive);

public interface IWebhookRegistrationRepository
{
    Task<WebhookRegistration?> GetAsync(WebhookRegistrationId id, CancellationToken cancellationToken);

    Task SaveAsync(WebhookRegistration registration, CancellationToken cancellationToken);
}

public interface IWebhookRegistrationOutboxWriter
{
    Task SaveAsync(
        WebhookRegistration registration,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken);
}

public interface IWebhookRegistrationReadRepository
{
    Task<IReadOnlyCollection<WebhookRegistrationReadModel>> GetWebhooksAsync(CancellationToken cancellationToken);
}

public interface IWebhookRegistrationProjection
{
    Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}

public sealed record WebhookAlertReadModel(
    Guid AlertId,
    Guid ProfileId,
    string ProfileName,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    DateOnly BillSubmittedOn,
    string AlertPolicy,
    IReadOnlyCollection<string> MatchedKeywords,
    DateTimeOffset CreatedAtUtc);

public interface IWebhookAlertReadRepository
{
    Task<IReadOnlyCollection<WebhookAlertReadModel>> GetAlertsAsync(CancellationToken cancellationToken);
}

public sealed record WebhookEventDispatchReadModel(
    Guid AlertId,
    Guid RegistrationId,
    string EventType,
    string CallbackUrl,
    DateTimeOffset DispatchedAtUtc);

public interface IWebhookEventDispatchStore
{
    Task<bool> HasDispatchedAsync(Guid alertId, Guid registrationId, string eventType, CancellationToken cancellationToken);

    Task SaveAsync(WebhookEventDispatchReadModel dispatch, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<WebhookEventDispatchReadModel>> GetDispatchesAsync(CancellationToken cancellationToken);
}

public sealed class WebhookRegistrationsCommandService(
    IWebhookRegistrationRepository repository,
    IWebhookRegistrationProjection projection)
{
    public async Task RegisterAsync(RegisterWebhookCommand command, CancellationToken cancellationToken)
    {
        var registrationId = new WebhookRegistrationId(command.RegistrationId);
        var existing = await repository.GetAsync(registrationId, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Webhook registration '{command.RegistrationId}' has already been created.");
        }

        var registration = WebhookRegistration.Register(
            registrationId,
            command.Name,
            WebhookCallbackUrl.Create(command.CallbackUrl),
            command.EventTypes.Select(WebhookEventType.Create),
            command.RequestedAtUtc);

        await SaveAndProjectAsync(registration, cancellationToken);
    }

    public async Task DeactivateAsync(DeactivateWebhookCommand command, CancellationToken cancellationToken)
    {
        var registration = await repository.GetAsync(new WebhookRegistrationId(command.RegistrationId), cancellationToken)
            ?? throw new InvalidOperationException($"Webhook registration '{command.RegistrationId}' was not found.");

        registration.Deactivate(command.RequestedAtUtc);
        await SaveAndProjectAsync(registration, cancellationToken);
    }

    public async Task UpdateAsync(UpdateWebhookCommand command, CancellationToken cancellationToken)
    {
        var registration = await repository.GetAsync(new WebhookRegistrationId(command.RegistrationId), cancellationToken)
            ?? throw new InvalidOperationException($"Webhook registration '{command.RegistrationId}' was not found.");

        registration.Update(
            command.Name,
            WebhookCallbackUrl.Create(command.CallbackUrl),
            command.EventTypes.Select(WebhookEventType.Create),
            command.RequestedAtUtc);
        await SaveAndProjectAsync(registration, cancellationToken);
    }

    private async Task SaveAndProjectAsync(WebhookRegistration registration, CancellationToken cancellationToken)
    {
        var pendingEvents = registration.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var integrationEvents = new List<IIntegrationEvent>(pendingEvents.Length);
        foreach (var domainEvent in pendingEvents)
        {
            switch (domainEvent)
            {
                case WebhookRegistered registered:
                    integrationEvents.Add(new WebhookRegisteredIntegrationEvent(
                        registered.EventId,
                        registered.OccurredAtUtc,
                        registered.RegistrationId.Value,
                        registered.Name,
                        registered.CallbackUrl,
                        registered.EventTypes.ToArray()));
                    break;
                case WebhookUpdated updated:
                    integrationEvents.Add(new WebhookUpdatedIntegrationEvent(
                        updated.EventId,
                        updated.OccurredAtUtc,
                        updated.RegistrationId.Value,
                        updated.Name,
                        updated.CallbackUrl,
                        updated.EventTypes.ToArray()));
                    break;
                case WebhookDeactivated deactivated:
                    integrationEvents.Add(new WebhookDeactivatedIntegrationEvent(
                        deactivated.EventId,
                        deactivated.OccurredAtUtc,
                        deactivated.RegistrationId.Value));
                    break;
            }
        }

        if (repository is IWebhookRegistrationOutboxWriter outboxWriter && integrationEvents.Count != 0)
        {
            await outboxWriter.SaveAsync(registration, integrationEvents, cancellationToken);
        }
        else
        {
            await repository.SaveAsync(registration, cancellationToken);
        }

        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }
}

public sealed class WebhookRegistrationsQueryService(IWebhookRegistrationReadRepository repository)
{
    public async Task<IReadOnlyList<WebhookRegistrationResponse>> GetWebhooksAsync(CancellationToken cancellationToken)
    {
        var webhooks = await repository.GetWebhooksAsync(cancellationToken);

        return webhooks
            .OrderBy(webhook => webhook.Name, StringComparer.OrdinalIgnoreCase)
            .Select(webhook => new WebhookRegistrationResponse(
                webhook.Id,
                webhook.Name,
                webhook.CallbackUrl,
                webhook.EventTypes.ToArray(),
                webhook.IsActive))
            .ToArray();
    }
}

public sealed record AlertWebhookDispatchResult(int ProcessedCount);

public sealed class AlertWebhookDispatchService(
    IWebhookAlertReadRepository alertRepository,
    IWebhookRegistrationReadRepository registrationRepository,
    IWebhookDispatcher dispatcher,
    IWebhookEventDispatchStore dispatchStore)
{
    private const string AlertCreatedEventType = "alert.created";

    public async Task<AlertWebhookDispatchResult> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var alerts = await alertRepository.GetAlertsAsync(cancellationToken);
        var registrations = await registrationRepository.GetWebhooksAsync(cancellationToken);
        var processedCount = 0;

        foreach (var alert in alerts.OrderBy(alert => alert.CreatedAtUtc))
        {
            var alertResult = await DispatchAlertAsync(alert, registrations, cancellationToken);
            processedCount += alertResult.ProcessedCount;
        }

        return new AlertWebhookDispatchResult(processedCount);
    }

    public async Task<AlertWebhookDispatchResult> DispatchAlertAsync(
        WebhookAlertReadModel alert,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var registrations = await registrationRepository.GetWebhooksAsync(cancellationToken);
        return await DispatchAlertAsync(alert, registrations, cancellationToken);
    }

    private async Task<AlertWebhookDispatchResult> DispatchAlertAsync(
        WebhookAlertReadModel alert,
        IReadOnlyCollection<WebhookRegistrationReadModel> registrations,
        CancellationToken cancellationToken)
    {
        var processedCount = 0;

        foreach (var registration in registrations.Where(IsAlertWebhook))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await dispatchStore.HasDispatchedAsync(alert.AlertId, registration.Id, AlertCreatedEventType, cancellationToken))
            {
                continue;
            }

            var payload = JsonSerializer.Serialize(new
            {
                type = AlertCreatedEventType,
                occurredAtUtc = alert.CreatedAtUtc,
                alert = new
                {
                    id = alert.AlertId,
                    profileId = alert.ProfileId,
                    profileName = alert.ProfileName,
                    billId = alert.BillId,
                    billTitle = alert.BillTitle,
                    billExternalId = alert.BillExternalId,
                    billSubmittedOn = alert.BillSubmittedOn,
                    alertPolicy = alert.AlertPolicy,
                    matchedKeywords = alert.MatchedKeywords
                }
            });

            await dispatcher.DispatchAsync(new WebhookDispatchRequest(
                registration.CallbackUrl,
                AlertCreatedEventType,
                payload,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-LawWatcher-Signature"] = "sha256=local-dev",
                    ["X-LawWatcher-Event-Type"] = AlertCreatedEventType
                }), cancellationToken);

            await dispatchStore.SaveAsync(new WebhookEventDispatchReadModel(
                alert.AlertId,
                registration.Id,
                AlertCreatedEventType,
                registration.CallbackUrl,
                DateTimeOffset.UtcNow), cancellationToken);

            processedCount++;
        }

        return new AlertWebhookDispatchResult(processedCount);
    }

    private static bool IsAlertWebhook(WebhookRegistrationReadModel registration)
    {
        return registration.IsActive &&
               registration.EventTypes.Contains(AlertCreatedEventType, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class WebhookEventDispatchesQueryService(IWebhookEventDispatchStore store)
{
    public async Task<IReadOnlyList<WebhookEventDispatchResponse>> GetDispatchesAsync(CancellationToken cancellationToken)
    {
        var dispatches = await store.GetDispatchesAsync(cancellationToken);

        return dispatches
            .OrderByDescending(dispatch => dispatch.DispatchedAtUtc)
            .ThenBy(dispatch => dispatch.CallbackUrl, StringComparer.OrdinalIgnoreCase)
            .Select(dispatch => new WebhookEventDispatchResponse(
                dispatch.AlertId,
                dispatch.RegistrationId,
                dispatch.EventType,
                dispatch.CallbackUrl,
                dispatch.DispatchedAtUtc))
            .ToArray();
    }
}
