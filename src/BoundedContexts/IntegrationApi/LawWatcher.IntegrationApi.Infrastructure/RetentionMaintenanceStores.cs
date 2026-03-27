using LawWatcher.IntegrationApi.Application;
using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;

namespace LawWatcher.IntegrationApi.Infrastructure;

public sealed class DisabledRetentionMaintenanceStore : IRetentionMaintenanceStore
{
    private const string SearchDocumentsRetentionReason = "search_documents retention is unavailable outside the SQL-backed maintenance runtime.";

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
            SearchDocumentsRetentionReason));
    }
}

public sealed class SqlServerRetentionMaintenanceStore(
    string connectionString,
    string schema = "lawwatcher") : IRetentionMaintenanceStore
{
    private const string SearchDocumentsRetentionSkippedReason = "search_documents retention was not requested.";
    private const string SearchDocumentsRetentionAppliedReason = "search_documents older than the requested retention window were pruned by indexed_at_utc.";

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

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var deletedPublishedOutboxCount = await DeletePublishedOutboxAsync(connection, transaction, publishedOutboxCutoffUtc, cancellationToken);
        var deletedProcessedInboxCount = await DeleteProcessedInboxAsync(connection, transaction, processedInboxCutoffUtc, cancellationToken);
        var deletedEventFeedCount = await DeleteEventFeedAsync(connection, transaction, eventFeedCutoffUtc, cancellationToken);
        var deletedSearchDocumentsCount = searchDocumentsCutoffUtc is null
            ? 0
            : await DeleteSearchDocumentsAsync(connection, transaction, searchDocumentsCutoffUtc.Value, cancellationToken);

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
                : SearchDocumentsRetentionSkippedReason);
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
