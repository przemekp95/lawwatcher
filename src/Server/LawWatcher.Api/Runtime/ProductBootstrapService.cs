using System.Security.Cryptography;
using LawWatcher.IdentityAndAccess.Application;
using LawWatcher.IdentityAndAccess.Contracts;

namespace LawWatcher.Api.Runtime;

public sealed class ProductBootstrapService(
    IOperatorAccountReadRepository operatorReadRepository,
    OperatorAccountsCommandService operatorCommandService,
    IApiClientReadRepository apiClientReadRepository,
    ApiClientsCommandService apiClientsCommandService,
    IApiTokenFingerprintService tokenFingerprintService)
{
    private static readonly string[] DefaultOperatorPermissions =
    [
        "operators:write",
        "profiles:write",
        "subscriptions:write",
        "webhooks:write",
        "api-clients:write"
    ];

    public async Task<BootstrapStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        var operators = await operatorReadRepository.GetOperatorsAsync(cancellationToken);
        var apiClients = await apiClientReadRepository.GetApiClientsAsync(cancellationToken);
        var hasOperators = operators.Count != 0;
        var hasApiClients = apiClients.Count != 0;

        return new BootstrapStatusResponse(
            RequiresOperatorBootstrap: !hasOperators,
            CanBootstrapApiClient: hasOperators && !hasApiClients,
            HasOperators: hasOperators,
            HasApiClients: hasApiClients);
    }

    public async Task<OperatorAccountResponse> BootstrapFirstOperatorAsync(
        BootstrapOperatorRequest request,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.RequiresOperatorBootstrap)
        {
            throw new InvalidOperationException("Operator bootstrap is available only before the first operator account exists.");
        }

        var operatorId = Guid.NewGuid();
        await operatorCommandService.RegisterAsync(
            new RegisterOperatorAccountCommand(
                operatorId,
                request.Email,
                request.DisplayName,
                request.Password,
                DefaultOperatorPermissions),
            cancellationToken);

        return await GetRequiredOperatorAsync(operatorId, cancellationToken);
    }

    public async Task<BootstrapApiClientResponse> BootstrapInitialApiClientAsync(
        BootstrapApiClientRequest request,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (status.RequiresOperatorBootstrap)
        {
            throw new InvalidOperationException("Create the first operator before bootstrapping an API client.");
        }

        if (!status.CanBootstrapApiClient)
        {
            throw new InvalidOperationException("API client bootstrap is available only before the first API client exists.");
        }

        var token = GenerateApiToken();
        var apiClientId = Guid.NewGuid();
        await apiClientsCommandService.RegisterAsync(
            new RegisterApiClientCommand(
                apiClientId,
                request.Name,
                request.ClientIdentifier,
                tokenFingerprintService.CreateFingerprint(token),
                request.Scopes),
            cancellationToken);

        return new BootstrapApiClientResponse(
            apiClientId,
            request.Name,
            request.ClientIdentifier,
            token,
            request.Scopes);
    }

    private async Task<OperatorAccountResponse> GetRequiredOperatorAsync(
        Guid operatorId,
        CancellationToken cancellationToken)
    {
        var operators = await operatorReadRepository.GetOperatorsAsync(cancellationToken);
        var createdOperator = operators.SingleOrDefault(candidate => candidate.Id == operatorId)
            ?? throw new InvalidOperationException($"Bootstrapped operator '{operatorId}' was not projected.");

        return new OperatorAccountResponse(
            createdOperator.Id,
            createdOperator.Email,
            createdOperator.DisplayName,
            createdOperator.Permissions
                .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            createdOperator.IsActive,
            createdOperator.RegisteredAtUtc);
    }

    private static string GenerateApiToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
