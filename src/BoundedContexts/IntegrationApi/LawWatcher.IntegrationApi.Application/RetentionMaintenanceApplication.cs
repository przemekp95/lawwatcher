using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Contracts;

namespace LawWatcher.IntegrationApi.Application;

public sealed record RunRetentionMaintenanceCommand(
    int PublishedOutboxRetentionHours,
    int ProcessedInboxRetentionHours,
    int EventFeedRetentionHours,
    int? SearchDocumentsRetentionHours) : Command;

public sealed record RetentionMaintenancePolicy(
    TimeSpan PublishedOutboxRetention,
    TimeSpan ProcessedInboxRetention,
    TimeSpan EventFeedRetention,
    TimeSpan? SearchDocumentsRetention);

public sealed record RetentionMaintenanceExecutionResult(
    bool MaintenanceAvailable,
    DateTimeOffset ExecutedAtUtc,
    DateTimeOffset PublishedOutboxCutoffUtc,
    DateTimeOffset ProcessedInboxCutoffUtc,
    DateTimeOffset EventFeedCutoffUtc,
    int DeletedPublishedOutboxCount,
    int DeletedProcessedInboxCount,
    int DeletedEventFeedCount,
    DateTimeOffset? SearchDocumentsCutoffUtc,
    int DeletedSearchDocumentsCount,
    bool SearchDocumentsRetentionApplied,
    string SearchDocumentsRetentionReason);

public interface IRetentionMaintenanceStore
{
    bool IsAvailable { get; }

    Task<RetentionMaintenanceExecutionResult> RunAsync(
        RetentionMaintenancePolicy policy,
        DateTimeOffset executedAtUtc,
        CancellationToken cancellationToken);
}

public sealed class RetentionMaintenanceCommandService(IRetentionMaintenanceStore store)
{
    public bool IsAvailable => store.IsAvailable;

    public async Task<RetentionMaintenanceResponse> RunAsync(
        RunRetentionMaintenanceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var policy = new RetentionMaintenancePolicy(
            ValidateHours(command.PublishedOutboxRetentionHours, nameof(command.PublishedOutboxRetentionHours), "Published outbox retention"),
            ValidateHours(command.ProcessedInboxRetentionHours, nameof(command.ProcessedInboxRetentionHours), "Processed inbox retention"),
            ValidateHours(command.EventFeedRetentionHours, nameof(command.EventFeedRetentionHours), "Event-feed retention"),
            ValidateOptionalHours(command.SearchDocumentsRetentionHours, nameof(command.SearchDocumentsRetentionHours), "Search-document retention"));
        var execution = await store.RunAsync(policy, command.RequestedAtUtc, cancellationToken);

        return new RetentionMaintenanceResponse(
            execution.MaintenanceAvailable,
            execution.ExecutedAtUtc,
            execution.PublishedOutboxCutoffUtc,
            execution.ProcessedInboxCutoffUtc,
            execution.EventFeedCutoffUtc,
            execution.DeletedPublishedOutboxCount,
            execution.DeletedProcessedInboxCount,
            execution.DeletedEventFeedCount,
            execution.SearchDocumentsCutoffUtc,
            execution.DeletedSearchDocumentsCount,
            execution.SearchDocumentsRetentionApplied,
            execution.SearchDocumentsRetentionReason);
    }

    private static TimeSpan ValidateHours(int hours, string paramName, string label)
    {
        if (hours <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, hours, $"{label} must be greater than zero hours.");
        }

        return TimeSpan.FromHours(hours);
    }

    private static TimeSpan? ValidateOptionalHours(int? hours, string paramName, string label)
    {
        if (hours is null)
        {
            return null;
        }

        return ValidateHours(hours.Value, paramName, label);
    }
}
