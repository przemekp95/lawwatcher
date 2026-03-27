namespace LawWatcher.BuildingBlocks.Configuration;

public sealed class StateStorageOptions
{
    public string Provider { get; init; } = "files";

    public string StateRoot { get; init; } = Path.Combine("..", "..", "..", "artifacts", "state");

    public string SqlServerConnectionStringName { get; init; } = "LawWatcherSqlServer";
}

public sealed record LawWatcherStatePaths(
    string Root,
    string AiTasksRoot,
    string ReplaysRoot,
    string BackfillsRoot,
    string ProfileSubscriptionsRoot,
    string WebhookRegistrationsRoot,
    string BillAlertsRoot,
    string NotificationDispatchesRoot,
    string WebhookEventDispatchesRoot,
    string MonitoringProfilesRoot,
    string BillsRoot,
    string ProcessesRoot,
    string ActsRoot,
    string ApiClientsRoot,
    string OperatorAccountsRoot,
    string EventFeedRoot,
    string SearchIndexRoot)
{
    public static LawWatcherStatePaths ForRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var normalizedRoot = Path.GetFullPath(root);
        return new LawWatcherStatePaths(
            normalizedRoot,
            Path.Combine(normalizedRoot, "ai-enrichment", "tasks"),
            Path.Combine(normalizedRoot, "integration-api", "replays"),
            Path.Combine(normalizedRoot, "integration-api", "backfills"),
            Path.Combine(normalizedRoot, "taxonomy-and-profiles", "subscriptions"),
            Path.Combine(normalizedRoot, "integration-api", "webhook-registrations"),
            Path.Combine(normalizedRoot, "notifications", "bill-alerts"),
            Path.Combine(normalizedRoot, "notifications", "dispatches"),
            Path.Combine(normalizedRoot, "integration-api", "webhook-dispatches"),
            Path.Combine(normalizedRoot, "taxonomy-and-profiles", "monitoring-profiles"),
            Path.Combine(normalizedRoot, "legislative-intake", "bills"),
            Path.Combine(normalizedRoot, "legislative-process", "processes"),
            Path.Combine(normalizedRoot, "legal-corpus", "acts"),
            Path.Combine(normalizedRoot, "identity-and-access", "api-clients"),
            Path.Combine(normalizedRoot, "identity-and-access", "operator-accounts"),
            Path.Combine(normalizedRoot, "integration-api", "events"),
            Path.Combine(normalizedRoot, "search", "documents"));
    }
}

public static class StateStoragePathResolver
{
    public static string ResolveRoot(StateStorageOptions options, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var configuredRoot = string.IsNullOrWhiteSpace(options.StateRoot)
            ? Path.Combine("..", "..", "..", "artifacts", "state")
            : options.StateRoot;

        var combinedPath = Path.IsPathRooted(configuredRoot)
            ? configuredRoot
            : Path.Combine(contentRootPath, configuredRoot);

        return Path.GetFullPath(combinedPath);
    }
}
