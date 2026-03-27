using LawWatcher.IntegrationApi.Application;
using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;

namespace LawWatcher.IntegrationApi.Infrastructure;

public sealed class DisabledRetentionMaintenanceStore : IRetentionMaintenanceStore
{
    private const string SearchDocumentsRetentionReason = "search_documents retention is unavailable outside the SQL-backed maintenance runtime.";
    private const string AiTasksRetentionReason = "ai_enrichment_tasks retention is unavailable outside the SQL-backed maintenance runtime.";
    private const string DocumentArtifactsRetentionReason = "Document artifact retention is not available because the current runtime does not track safe artifact ownership or expiry metadata.";

    public bool IsAvailable => false;

    public Task<RetentionMaintenanceExecutionResult> RunAsync(
        RetentionMaintenancePolicy policy,
        DateTimeOffset executedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new RetentionMaintenanceExecutionResult(
            false,
            executedAtUtc,
            executedAtUtc - policy.PublishedOutboxRetention,
            executedAtUtc - policy.ProcessedInboxRetention,
            executedAtUtc - policy.EventFeedRetention,
            0,
            0,
            0,
            policy.SearchDocumentsRetention is null ? null : executedAtUtc - policy.SearchDocumentsRetention.Value,
            0,
            false,
            SearchDocumentsRetentionReason,
            policy.AiTasksRetention is null ? null : executedAtUtc - policy.AiTasksRetention.Value,
            0,
            false,
            AiTasksRetentionReason,
            policy.DocumentArtifactsRetention is null ? null : executedAtUtc - policy.DocumentArtifactsRetention.Value,
            0,
            false,
            DocumentArtifactsRetentionReason));
    }
}

public sealed class SqlServerRetentionMaintenanceStore(
    string connectionString,
    string schema = "lawwatcher") : IRetentionMaintenanceStore
{
    private const string SearchDocumentsRetentionSkippedReason = "search_documents retention was not requested.";
    private const string SearchDocumentsRetentionAppliedReason = "search_documents older than the requested retention window were pruned by indexed_at_utc.";
    private const string AiTasksRetentionSkippedReason = "ai_enrichment_tasks retention was not requested.";
    private const string AiTasksRetentionAppliedReason = "completed and failed ai_enrichment_tasks older than the requested retention window were pruned by terminal timestamps.";
    private const string DocumentArtifactsRetentionSkippedReason = "Document artifact retention was not requested.";
    private const string DocumentArtifactsRetentionUnavailableReason = "Document artifact retention is not available because the current runtime does not track safe artifact ownership or expiry metadata.";

    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public bool IsAvailable => true;

    public async Task<RetentionMaintenanceExecutionResult> RunAsync(
        RetentionMaintenancePolicy policy,
        DateTimeOffset executedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var publishedOutboxCutoffUtc = executedAtUtc - policy.PublishedOutboxRetention;
        var processedInboxCutoffUtc = executedAtUtc - policy.ProcessedInboxRetention;
        var eventFeedCutoffUtc = executedAtUtc - policy.EventFeedRetention;
        DateTimeOffset? searchDocumentsCutoffUtc = policy.SearchDocumentsRetention is null
            ? null
            : executedAtUtc - policy.SearchDocumentsRetention.Value;
        DateTimeOffset? aiTasksCutoffUtc = policy.AiTasksRetention is null
            ? null
            : executedAtUtc - policy.AiTasksRetention.Value;
        DateTimeOffset? documentArtifactsCutoffUtc = policy.DocumentArtifactsRetention is null
            ? null
            : executedAtUtc - policy.DocumentArtifactsRetention.Value;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var deletedPublishedOutboxCount = await DeletePublishedOutboxAsync(connection, transaction, publishedOutboxCutoffUtc, cancellationToken);
        var deletedProcessedInboxCount = await DeleteProcessedInboxAsync(connection, transaction, processedInboxCutoffUtc, cancellationToken);
        var deletedEventFeedCount = await DeleteEventFeedAsync(connection, transaction, eventFeedCutoffUtc, cancellationToken);
        var deletedSearchDocumentsCount = searchDocumentsCutoffUtc is null
            ? 0
            : await DeleteSearchDocumentsAsync(connection, transaction, searchDocumentsCutoffUtc.Value, cancellationToken);
        var deletedAiTasksCount = aiTasksCutoffUtc is null
            ? 0
            : await DeleteAiTasksAsync(connection, transaction, aiTasksCutoffUtc.Value, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new RetentionMaintenanceExecutionResult(
            true,
            executedAtUtc,
            publishedOutboxCutoffUtc,
            processedInboxCutoffUtc,
            eventFeedCutoffUtc,
            deletedPublishedOutboxCount,
            deletedProcessedInboxCount,
            deletedEventFeedCount,
            searchDocumentsCutoffUtc,
            deletedSearchDocumentsCount,
            searchDocumentsCutoffUtc is not null,
            searchDocumentsCutoffUtc is not null
                ? SearchDocumentsRetentionAppliedReason
                : SearchDocumentsRetentionSkippedReason,
            aiTasksCutoffUtc,
            deletedAiTasksCount,
            aiTasksCutoffUtc is not null,
            aiTasksCutoffUtc is not null
                ? AiTasksRetentionAppliedReason
                : AiTasksRetentionSkippedReason,
            documentArtifactsCutoffUtc,
            0,
            false,
            documentArtifactsCutoffUtc is not null
                ? DocumentArtifactsRetentionUnavailableReason
                : DocumentArtifactsRetentionSkippedReason);
    }

    private async Task<int> DeletePublishedOutboxAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            DELETE FROM [{_schema}].[outbox]
            WHERE [status] = 'published'
              AND [published_at_utc] IS NOT NULL
              AND [published_at_utc] < @cutoffUtc;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@cutoffUtc", cutoffUtc.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> DeleteProcessedInboxAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            DELETE FROM [{_schema}].[inbox]
            WHERE [status] = 'processed'
              AND COALESCE([processed_at_utc], [received_at_utc]) < @cutoffUtc;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@cutoffUtc", cutoffUtc.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> DeleteEventFeedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            DELETE FROM [{_schema}].[event_feed]
            WHERE [occurred_at_utc] < @cutoffUtc;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@cutoffUtc", cutoffUtc.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> DeleteSearchDocumentsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            DELETE FROM [{_schema}].[search_documents]
            WHERE [indexed_at_utc] < @cutoffUtc;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@cutoffUtc", cutoffUtc.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> DeleteAiTasksAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            DELETE FROM [{_schema}].[ai_enrichment_tasks]
            WHERE [status] IN ('completed', 'failed')
              AND COALESCE([completed_at_utc], [failed_at_utc], [requested_at_utc]) < @cutoffUtc;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@cutoffUtc", cutoffUtc.UtcDateTime);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ValidateSchema(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (!Regex.IsMatch(schema, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw new ArgumentOutOfRangeException(nameof(schema), "SQL schema name contains unsupported characters.");
        }

        return schema;
    }
}
