using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.IdentityAndAccess.Application;
using Microsoft.Extensions.Options;

namespace LawWatcher.Api.Runtime;

public sealed class OperatorAccountsBootstrapHostedService(
    IOptions<BootstrapOptions> options,
    OperatorAccountsQueryService queryService,
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
        var bootstrapOptions = options.Value;
        if (!bootstrapOptions.EnableInitialOperator)
        {
            return;
        }

        var existingOperators = await queryService.GetOperatorsAsync(cancellationToken);
        if (existingOperators.Count != 0)
        {
            return;
        }

        var email = RequireValue(bootstrapOptions.InitialOperatorEmail, nameof(bootstrapOptions.InitialOperatorEmail));
        var displayName = RequireValue(bootstrapOptions.InitialOperatorDisplayName, nameof(bootstrapOptions.InitialOperatorDisplayName));
        var password = RequireValue(bootstrapOptions.InitialOperatorPassword, nameof(bootstrapOptions.InitialOperatorPassword));

        await commandService.RegisterAsync(new RegisterOperatorAccountCommand(
            Guid.Parse("A4E15148-7702-4C24-A7B4-291C93C138E3"),
            email,
            displayName,
            password,
            DefaultPermissions), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string RequireValue(string value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Bootstrap option '{optionName}' must be configured when initial operator bootstrap is enabled.");
        }

        return value.Trim();
    }
}
