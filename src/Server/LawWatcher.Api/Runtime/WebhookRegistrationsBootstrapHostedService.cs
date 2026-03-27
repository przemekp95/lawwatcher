using LawWatcher.IntegrationApi.Application;

namespace LawWatcher.Api.Runtime;

public sealed class WebhookRegistrationsBootstrapHostedService(
    WebhookRegistrationsQueryService queryService,
    WebhookRegistrationsCommandService commandService) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var existingWebhooks = await queryService.GetWebhooksAsync(cancellationToken);
        if (existingWebhooks.Count != 0)
        {
            return;
        }

        await commandService.RegisterAsync(new RegisterWebhookCommand(
            Guid.Parse("F57847E6-6358-4992-806F-0A4B202B421B"),
            "ERP sync",
            "https://erp.example.test/lawwatcher",
            ["alert.created", "process.updated"]), cancellationToken);
        await commandService.DeactivateAsync(new DeactivateWebhookCommand(
            Guid.Parse("F57847E6-6358-4992-806F-0A4B202B421B")), cancellationToken);
        await commandService.RegisterAsync(new RegisterWebhookCommand(
            Guid.Parse("5BFE3942-F65E-4805-A13C-8DF327E4C1F9"),
            "Portal audit",
            "https://audit.example.test/webhooks/lawwatcher",
            ["bill.imported"]), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
