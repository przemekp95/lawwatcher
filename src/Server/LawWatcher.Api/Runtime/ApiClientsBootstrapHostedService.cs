using LawWatcher.IdentityAndAccess.Application;

namespace LawWatcher.Api.Runtime;

public sealed class ApiClientsBootstrapHostedService(
    ApiClientsQueryService queryService,
    ApiClientsCommandService commandService,
    IApiTokenFingerprintService tokenFingerprintService) : IHostedService
{
    private const string ErpExportToken = "erp-export-demo-token";
    private const string PortalIntegratorToken = "portal-integrator-demo-token";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var existingClients = await queryService.GetApiClientsAsync(cancellationToken);
        if (existingClients.Count != 0)
        {
            return;
        }

        await commandService.RegisterAsync(new RegisterApiClientCommand(
            Guid.Parse("F3E8F9CA-7345-42CB-B510-F295A5E738B3"),
            "ERP Export",
            "erp-export",
            tokenFingerprintService.CreateFingerprint(ErpExportToken),
            ["alerts:read", "replays:write"]), cancellationToken);
        await commandService.DeactivateAsync(new DeactivateApiClientCommand(
            Guid.Parse("F3E8F9CA-7345-42CB-B510-F295A5E738B3")), cancellationToken);
        await commandService.RegisterAsync(new RegisterApiClientCommand(
            Guid.Parse("532AD21A-FF6D-4665-9F88-6B0295C4D6A2"),
            "Portal Integrator",
            "portal-integrator",
            tokenFingerprintService.CreateFingerprint(PortalIntegratorToken),
            ["search:read", "replays:write", "backfills:write", "ai:write", "webhooks:write", "profiles:write", "subscriptions:write", "api-clients:write"]), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
