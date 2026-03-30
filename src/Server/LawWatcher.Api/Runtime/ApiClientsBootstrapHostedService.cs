using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.IdentityAndAccess.Application;
using Microsoft.Extensions.Options;

namespace LawWatcher.Api.Runtime;

public sealed class ApiClientsBootstrapHostedService(
    IOptions<BootstrapOptions> options,
    ApiClientsQueryService queryService,
    ApiClientsCommandService commandService,
    IApiTokenFingerprintService tokenFingerprintService) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var bootstrapOptions = options.Value;
        if (!bootstrapOptions.EnableInitialApiClient)
        {
            return;
        }

        var existingClients = await queryService.GetApiClientsAsync(cancellationToken);
        if (existingClients.Count != 0)
        {
            return;
        }

        await commandService.RegisterAsync(new RegisterApiClientCommand(
            Guid.Parse("532AD21A-FF6D-4665-9F88-6B0295C4D6A2"),
            RequireValue(bootstrapOptions.InitialApiClientName, nameof(bootstrapOptions.InitialApiClientName)),
            RequireValue(bootstrapOptions.InitialApiClientIdentifier, nameof(bootstrapOptions.InitialApiClientIdentifier)),
            tokenFingerprintService.CreateFingerprint(RequireValue(bootstrapOptions.InitialApiClientToken, nameof(bootstrapOptions.InitialApiClientToken))),
            RequireScopes(bootstrapOptions.InitialApiClientScopesCsv, nameof(bootstrapOptions.InitialApiClientScopesCsv))), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string RequireValue(string value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Bootstrap option '{optionName}' must be configured when initial API client bootstrap is enabled.");
        }

        return value.Trim();
    }

    private static IReadOnlyCollection<string> RequireScopes(string scopesCsv, string optionName)
    {
        var normalized = scopesCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new InvalidOperationException($"Bootstrap option '{optionName}' must contain at least one scope when initial API client bootstrap is enabled.");
        }

        return normalized;
    }
}
