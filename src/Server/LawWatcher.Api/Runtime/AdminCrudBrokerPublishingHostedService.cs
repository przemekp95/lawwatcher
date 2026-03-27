using LawWatcher.IntegrationApi.Application;
using LawWatcher.TaxonomyAndProfiles.Application;

namespace LawWatcher.Api.Runtime;

public sealed class AdminCrudBrokerPublishingHostedService(
    ILogger<AdminCrudBrokerPublishingHostedService> logger,
    ProfileSubscriptionOutboxPublisher profileSubscriptionOutboxPublisher,
    WebhookRegistrationOutboxPublisher webhookRegistrationOutboxPublisher) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var profileSubscriptionBatch = await profileSubscriptionOutboxPublisher.PublishPendingAsync(maxMessages: 16, stoppingToken);
            var webhookRegistrationBatch = await webhookRegistrationOutboxPublisher.PublishPendingAsync(maxMessages: 16, stoppingToken);

            if (profileSubscriptionBatch.PublishedCount > 0)
            {
                logger.LogInformation(
                    "api broker publish batch completed. flow=profile-subscription published={PublishedCount} hasRemainingMessages={HasRemainingMessages}",
                    profileSubscriptionBatch.PublishedCount,
                    profileSubscriptionBatch.HasRemainingMessages);
            }

            if (webhookRegistrationBatch.PublishedCount > 0)
            {
                logger.LogInformation(
                    "api broker publish batch completed. flow=webhook-registration published={PublishedCount} hasRemainingMessages={HasRemainingMessages}",
                    webhookRegistrationBatch.PublishedCount,
                    webhookRegistrationBatch.HasRemainingMessages);
            }

            if (profileSubscriptionBatch.PublishedCount > 0 || webhookRegistrationBatch.PublishedCount > 0)
            {
                if (profileSubscriptionBatch.HasRemainingMessages || webhookRegistrationBatch.HasRemainingMessages)
                {
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }
}
