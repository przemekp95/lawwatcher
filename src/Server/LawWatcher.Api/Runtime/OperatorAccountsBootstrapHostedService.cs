using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.IdentityAndAccess.Application;
using Microsoft.Extensions.Options;

namespace LawWatcher.Api.Runtime;

public sealed class OperatorAccountsBootstrapHostedService(
    IOptions<SeedDataOptions> options,
    IOperatorAccountReadRepository readRepository,
    OperatorAccountsCommandService commandService) : IHostedService
{
    private static readonly string[] DefaultPermissions =
    [
        "operators:write",
        "profiles:write",
        "subscriptions:write",
        "webhooks:write",
        "api-clients:write"
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seedOptions = options.Value;
        if (!seedOptions.EnableDefaultOperatorSeed)
        {
            return;
        }

        var existing = await readRepository.GetByEmailAsync(seedOptions.DefaultOperatorEmail, cancellationToken);
        if (existing is not null)
        {
            return;
        }

        await commandService.RegisterAsync(new RegisterOperatorAccountCommand(
            Guid.Parse("A4E15148-7702-4C24-A7B4-291C93C138E3"),
            seedOptions.DefaultOperatorEmail,
            seedOptions.DefaultOperatorDisplayName,
            seedOptions.DefaultOperatorPassword,
            DefaultPermissions), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
