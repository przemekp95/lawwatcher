namespace LawWatcher.BuildingBlocks.Configuration;

public sealed class SeedDataOptions
{
    public bool EnableWebhookSubscriptionSeed { get; init; } = true;

    public bool EnableDefaultOperatorSeed { get; init; } = true;

    public string DefaultOperatorEmail { get; init; } = "admin@lawwatcher.local";

    public string DefaultOperatorDisplayName { get; init; } = "Local Admin";

    public string DefaultOperatorPassword { get; init; } = "Admin123!";
}
