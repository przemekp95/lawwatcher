using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IdentityAndAccess.Contracts;
using LawWatcher.IdentityAndAccess.Domain.ApiClients;

namespace LawWatcher.IdentityAndAccess.Application;

public sealed record RegisterApiClientCommand(
    Guid ApiClientId,
    string Name,
    string ClientIdentifier,
    string TokenFingerprint,
    IReadOnlyCollection<string> Scopes) : Command;

public sealed record DeactivateApiClientCommand(
    Guid ApiClientId) : Command;

public sealed record UpdateApiClientCommand(
    Guid ApiClientId,
    string Name,
    string? TokenFingerprint,
    IReadOnlyCollection<string> Scopes) : Command;

public sealed record ApiClientReadModel(
    Guid Id,
    string Name,
    string ClientIdentifier,
    string TokenFingerprint,
    IReadOnlyCollection<string> Scopes,
    bool IsActive,
    DateTimeOffset RegisteredAtUtc);

public interface IApiClientRepository
{
    Task<ApiClient?> GetAsync(ApiClientId id, CancellationToken cancellationToken);

    Task SaveAsync(ApiClient client, CancellationToken cancellationToken);
}

public interface IApiClientReadRepository
{
    Task<IReadOnlyCollection<ApiClientReadModel>> GetApiClientsAsync(CancellationToken cancellationToken);
}

public interface IApiClientProjection
{
    Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}

public interface IApiTokenFingerprintService
{
    string CreateFingerprint(string token);
}

public enum ApiClientAccessDecision
{
    UnknownToken = 0,
    InactiveClient = 1,
    MissingScope = 2,
    Authorized = 3
}

public sealed record ApiClientAccessResult(
    ApiClientAccessDecision Decision,
    Guid? ClientId,
    string? ClientIdentifier,
    string? Name);

public sealed class ApiClientsCommandService(
    IApiClientRepository repository,
    IApiClientProjection projection)
{
    public async Task RegisterAsync(RegisterApiClientCommand command, CancellationToken cancellationToken)
    {
        var clientId = new ApiClientId(command.ApiClientId);
        var existing = await repository.GetAsync(clientId, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"API client '{command.ApiClientId}' has already been registered.");
        }

        var client = ApiClient.Register(
            clientId,
            command.Name,
            ClientIdentifier.Create(command.ClientIdentifier),
            TokenFingerprint.Create(command.TokenFingerprint),
            command.Scopes.Select(ApiScope.Of).ToArray(),
            command.RequestedAtUtc);

        await SaveAndProjectAsync(client, cancellationToken);
    }

    public async Task DeactivateAsync(DeactivateApiClientCommand command, CancellationToken cancellationToken)
    {
        var client = await repository.GetAsync(new ApiClientId(command.ApiClientId), cancellationToken)
            ?? throw new InvalidOperationException($"API client '{command.ApiClientId}' was not found.");

        client.Deactivate(command.RequestedAtUtc);
        await SaveAndProjectAsync(client, cancellationToken);
    }

    public async Task UpdateAsync(UpdateApiClientCommand command, CancellationToken cancellationToken)
    {
        var client = await repository.GetAsync(new ApiClientId(command.ApiClientId), cancellationToken)
            ?? throw new InvalidOperationException($"API client '{command.ApiClientId}' was not found.");

        client.Update(
            command.Name,
            string.IsNullOrWhiteSpace(command.TokenFingerprint) ? null : TokenFingerprint.Create(command.TokenFingerprint),
            command.Scopes.Select(ApiScope.Of).ToArray(),
            command.RequestedAtUtc);
        await SaveAndProjectAsync(client, cancellationToken);
    }

    private async Task SaveAndProjectAsync(ApiClient client, CancellationToken cancellationToken)
    {
        var pendingEvents = client.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        await repository.SaveAsync(client, cancellationToken);
        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }
}

public sealed class ApiClientsQueryService(IApiClientReadRepository repository)
{
    public async Task<IReadOnlyList<ApiClientResponse>> GetApiClientsAsync(CancellationToken cancellationToken)
    {
        var clients = await repository.GetApiClientsAsync(cancellationToken);

        return clients
            .OrderBy(client => client.Name, StringComparer.OrdinalIgnoreCase)
            .Select(client => new ApiClientResponse(
                client.Id,
                client.Name,
                client.ClientIdentifier,
                client.TokenFingerprint,
                ApiClientScopeCatalog.NormalizeMany(client.Scopes),
                client.IsActive,
                client.RegisteredAtUtc))
            .ToArray();
    }
}

public sealed class ApiClientAccessService(
    IApiClientReadRepository repository,
    IApiTokenFingerprintService tokenFingerprintService)
{
    public async Task<ApiClientAccessResult> AuthorizeAsync(
        string bearerToken,
        string requiredScope,
        CancellationToken cancellationToken)
    {
        var tokenFingerprint = tokenFingerprintService.CreateFingerprint(bearerToken);
        var normalizedScope = NormalizeRequired(requiredScope, nameof(requiredScope), "Required scope");
        var clients = await repository.GetApiClientsAsync(cancellationToken);

        var client = clients.SingleOrDefault(candidate =>
            candidate.TokenFingerprint.Equals(tokenFingerprint, StringComparison.OrdinalIgnoreCase));

        if (client is null)
        {
            return new ApiClientAccessResult(ApiClientAccessDecision.UnknownToken, null, null, null);
        }

        if (!client.IsActive)
        {
            return new ApiClientAccessResult(
                ApiClientAccessDecision.InactiveClient,
                client.Id,
                client.ClientIdentifier,
                client.Name);
        }

        if (!client.Scopes.Contains(normalizedScope, StringComparer.OrdinalIgnoreCase))
        {
            return new ApiClientAccessResult(
                ApiClientAccessDecision.MissingScope,
                client.Id,
                client.ClientIdentifier,
                client.Name);
        }

        return new ApiClientAccessResult(
            ApiClientAccessDecision.Authorized,
            client.Id,
            client.ClientIdentifier,
            client.Name);
    }

    private static string NormalizeRequired(string value, string paramName, string label)
    {
        return ApiClientScopeCatalog.Normalize(value, paramName, label);
    }
}
