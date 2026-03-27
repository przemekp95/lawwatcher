namespace LawWatcher.BuildingBlocks.Configuration;

public sealed class SeedDataOptions
{
    public bool EnableWebhookSubscriptionSeed { get; init; } = true;

    public bool EnableDefaultOperatorSeed { get; init; } = false;

    public bool EnableDefaultApiClientSeed { get; init; } = false;

    public string DefaultOperatorEmail { get; init; } = "admin@lawwatcher.local";

    public string DefaultOperatorDisplayName { get; init; } = "Local Admin";

    public string DefaultOperatorPassword { get; init; } = "Admin123!";
}
