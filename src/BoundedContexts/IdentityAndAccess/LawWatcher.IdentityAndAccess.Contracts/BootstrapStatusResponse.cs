namespace LawWatcher.IdentityAndAccess.Contracts;

public sealed record BootstrapStatusResponse(
    bool RequiresOperatorBootstrap,
    bool CanBootstrapApiClient,
    bool HasOperators,
    bool HasApiClients);
