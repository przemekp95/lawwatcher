namespace LawWatcher.BuildingBlocks.Configuration;

public sealed class BootstrapOptions
{
    public string Secret { get; init; } = string.Empty;

    public bool EnableDemoData { get; init; } = true;

    public bool EnableInitialOperator { get; init; } = false;

    public string InitialOperatorEmail { get; init; } = string.Empty;

    public string InitialOperatorDisplayName { get; init; } = string.Empty;

    public string InitialOperatorPassword { get; init; } = string.Empty;

    public bool EnableInitialApiClient { get; init; } = false;

    public string InitialApiClientName { get; init; } = string.Empty;

    public string InitialApiClientIdentifier { get; init; } = string.Empty;

    public string InitialApiClientToken { get; init; } = string.Empty;

    public string InitialApiClientScopesCsv { get; init; } = string.Empty;
}
