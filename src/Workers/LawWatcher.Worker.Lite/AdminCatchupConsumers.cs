using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Contracts;
using LawWatcher.Notifications.Application;
using LawWatcher.TaxonomyAndProfiles.Contracts;
using MassTransit;

namespace LawWatcher.Worker.Lite;

public sealed class ProfileSubscriptionNotificationConsumer(
    ILogger<ProfileSubscriptionNotificationConsumer> logger,
    ProfileSubscriptionNotificationMessageHandler messageHandler) :
    IConsumer<ProfileSubscriptionCreatedIntegrationEvent>,
    IConsumer<ProfileSubscriptionAlertPolicyChangedIntegrationEvent>,
    IConsumer<ProfileSubscriptionDeactivatedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<ProfileSubscriptionCreatedIntegrationEvent> context)
    {
        var result = await messageHandler.HandleAsync(context.Message, context.CancellationToken);
        logger.LogInformation(
            "worker-lite broker message handled. flow=profile-subscription eventId={EventId} subscriptionId={SubscriptionId} processedDispatches={ProcessedDispatchCount} skippedDigest={SkippedDigestCount}",
            context.Message.EventId,
            context.Message.SubscriptionId,
            result.ProcessedCount,
            result.SkippedDigestCount);
    }

    public async Task Consume(ConsumeContext<ProfileSubscriptionAlertPolicyChangedIntegrationEvent> context)
    {
        var result = await messageHandler.HandleAsync(context.Message, context.CancellationToken);
        logger.LogInformation(
            "worker-lite broker message handled. flow=profile-subscription eventId={EventId} subscriptionId={SubscriptionId} processedDispatches={ProcessedDispatchCount} skippedDigest={SkippedDigestCount}",
            context.Message.EventId,
            context.Message.SubscriptionId,
            result.ProcessedCount,
            result.SkippedDigestCount);
    }

    public async Task Consume(ConsumeContext<ProfileSubscriptionDeactivatedIntegrationEvent> context)
    {
        var result = await messageHandler.HandleAsync(context.Message, context.CancellationToken);
        logger.LogInformation(
            "worker-lite broker message handled. flow=profile-subscription eventId={EventId} subscriptionId={SubscriptionId} processedDispatches={ProcessedDispatchCount} skippedDigest={SkippedDigestCount}",
            context.Message.EventId,
            context.Message.SubscriptionId,
            result.ProcessedCount,
            result.SkippedDigestCount);
    }
}

public sealed class WebhookRegistrationDispatchConsumer(
    ILogger<WebhookRegistrationDispatchConsumer> logger,
    WebhookRegistrationDispatchMessageHandler messageHandler) :
    IConsumer<WebhookRegisteredIntegrationEvent>,
    IConsumer<WebhookUpdatedIntegrationEvent>,
    IConsumer<WebhookDeactivatedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<WebhookRegisteredIntegrationEvent> context)
    {
        var result = await messageHandler.HandleAsync(context.Message, context.CancellationToken);
        logger.LogInformation(
            "worker-lite broker message handled. flow=webhook-registration eventId={EventId} registrationId={RegistrationId} processedDispatches={ProcessedDispatchCount}",
            context.Message.EventId,
            context.Message.RegistrationId,
            result.ProcessedCount);
    }

    public async Task Consume(ConsumeContext<WebhookUpdatedIntegrationEvent> context)
    {
        var result = await messageHandler.HandleAsync(context.Message, context.CancellationToken);
        logger.LogInformation(
            "worker-lite broker message handled. flow=webhook-registration eventId={EventId} registrationId={RegistrationId} processedDispatches={ProcessedDispatchCount}",
            context.Message.EventId,
            context.Message.RegistrationId,
            result.ProcessedCount);
    }

    public async Task Consume(ConsumeContext<WebhookDeactivatedIntegrationEvent> context)
    {
        var result = await messageHandler.HandleAsync(context.Message, context.CancellationToken);
        logger.LogInformation(
            "worker-lite broker message handled. flow=webhook-registration eventId={EventId} registrationId={RegistrationId} processedDispatches={ProcessedDispatchCount}",
            context.Message.EventId,
            context.Message.RegistrationId,
            result.ProcessedCount);
    }
}
