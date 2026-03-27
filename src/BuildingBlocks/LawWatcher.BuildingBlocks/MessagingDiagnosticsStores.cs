using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;

namespace LawWatcher.BuildingBlocks.Messaging;

public sealed class DisabledMessagingDiagnosticsStore : IMessagingDiagnosticsStore
{
    private static readonly MessagingDiagnosticsSnapshot EmptySnapshot = new(
        false,
        new OutboxDiagnosticsSnapshot(
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            null,
            Array.Empty<OutboxMessageTypeDiagnosticsSnapshot>()),
        new InboxDiagnosticsSnapshot(
            0,
            Array.Empty<InboxConsumerDiagnosticsSnapshot>()));

    public bool IsAvailable => false;

    public Task<MessagingDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EmptySnapshot);
    }
}

public sealed class SqlServerMessagingDiagnosticsStore : IMessagingDiagnosticsStore
{
    private readonly string _connectionString;
    private readonly string _schema;

    public SqlServerMessagingDiagnosticsStore(string connectionString, string schema = "lawwatcher")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _schema = ValidateSchema(schema);
    }

    public bool IsAvailable => true;

    public async Task<MessagingDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var outbox = await ReadOutboxDiagnosticsAsync(connection, cancellationToken);
        var inbox = await ReadInboxDiagnosticsAsync(connection, cancellationToken);
        return new MessagingDiagnosticsSnapshot(true, outbox, inbox);
    }

    private async Task<OutboxDiagnosticsSnapshot> ReadOutboxDiagnosticsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var summarySql = $"""
            SELECT
                COUNT(*) AS [total_count],
                ISNULL(SUM(CASE WHEN [status] = 'pending' THEN 1 ELSE 0 END), 0) AS [pending_count],
                ISNULL(SUM(CASE WHEN [status] = 'pending' AND ([next_attempt_at_utc] IS NULL OR [next_attempt_at_utc] <= SYSUTCDATETIME()) THEN 1 ELSE 0 END), 0) AS [ready_count],
                ISNULL(SUM(CASE WHEN [status] = 'pending' AND [next_attempt_at_utc] IS NOT NULL AND [next_attempt_at_utc] > SYSUTCDATETIME() THEN 1 ELSE 0 END), 0) AS [deferred_count],
                ISNULL(SUM(CASE WHEN [status] = 'published' THEN 1 ELSE 0 END), 0) AS [published_count],
                ISNULL(MAX([attempt_count]), 0) AS [max_attempt_count],
                MIN(CASE WHEN [status] = 'pending' THEN [created_at_utc] END) AS [oldest_pending_created_at_utc],
                MIN(CASE WHEN [status] = 'pending' AND [next_attempt_at_utc] IS NOT NULL AND [next_attempt_at_utc] > SYSUTCDATETIME() THEN [next_attempt_at_utc] END) AS [next_scheduled_attempt_at_utc]
            FROM [{_schema}].[outbox];
            """;

        await using var summaryCommand = new SqlCommand(summarySql, connection);
        await using var summaryReader = await summaryCommand.ExecuteReaderAsync(cancellationToken);
        await summaryReader.ReadAsync(cancellationToken);

        var totalCount = summaryReader.GetInt32(0);
        var pendingCount = summaryReader.GetInt32(1);
        var readyCount = summaryReader.GetInt32(2);
        var deferredCount = summaryReader.GetInt32(3);
        var publishedCount = summaryReader.GetInt32(4);
        var maxAttemptCount = summaryReader.GetInt32(5);
        var oldestPendingCreatedAtUtc = ReadNullableDateTimeOffset(summaryReader, 6);
        var nextScheduledAttemptAtUtc = ReadNullableDateTimeOffset(summaryReader, 7);

        var messageTypes = new List<OutboxMessageTypeDiagnosticsSnapshot>();
        await summaryReader.CloseAsync();

        var messageTypeSql = $"""
            SELECT
                [message_type],
                COUNT(*) AS [total_count],
                ISNULL(SUM(CASE WHEN [status] = 'pending' THEN 1 ELSE 0 END), 0) AS [pending_count],
                ISNULL(SUM(CASE WHEN [status] = 'pending' AND ([next_attempt_at_utc] IS NULL OR [next_attempt_at_utc] <= SYSUTCDATETIME()) THEN 1 ELSE 0 END), 0) AS [ready_count],
                ISNULL(SUM(CASE WHEN [status] = 'pending' AND [next_attempt_at_utc] IS NOT NULL AND [next_attempt_at_utc] > SYSUTCDATETIME() THEN 1 ELSE 0 END), 0) AS [deferred_count],
                ISNULL(SUM(CASE WHEN [status] = 'published' THEN 1 ELSE 0 END), 0) AS [published_count],
                ISNULL(MAX([attempt_count]), 0) AS [max_attempt_count]
            FROM [{_schema}].[outbox]
            GROUP BY [message_type]
            ORDER BY [pending_count] DESC, [total_count] DESC, [message_type] ASC;
            """;

        await using var messageTypeCommand = new SqlCommand(messageTypeSql, connection);
        await using var messageTypeReader = await messageTypeCommand.ExecuteReaderAsync(cancellationToken);
        while (await messageTypeReader.ReadAsync(cancellationToken))
        {
            messageTypes.Add(new OutboxMessageTypeDiagnosticsSnapshot(
                messageTypeReader.GetString(0),
                messageTypeReader.GetInt32(1),
                messageTypeReader.GetInt32(2),
                messageTypeReader.GetInt32(3),
                messageTypeReader.GetInt32(4),
                messageTypeReader.GetInt32(5),
                messageTypeReader.GetInt32(6)));
        }

        return new OutboxDiagnosticsSnapshot(
            totalCount,
            pendingCount,
            readyCount,
            deferredCount,
            publishedCount,
            maxAttemptCount,
            oldestPendingCreatedAtUtc,
            nextScheduledAttemptAtUtc,
            messageTypes);
    }

    private async Task<InboxDiagnosticsSnapshot> ReadInboxDiagnosticsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var summarySql = $"""
            SELECT ISNULL(COUNT(*), 0)
            FROM [{_schema}].[inbox]
            WHERE [status] = 'processed';
            """;

        await using var summaryCommand = new SqlCommand(summarySql, connection);
        var processedCount = Convert.ToInt32(await summaryCommand.ExecuteScalarAsync(cancellationToken));

        var consumerSql = $"""
            SELECT
                [consumer_name],
                COUNT(*) AS [processed_count],
                MAX([processed_at_utc]) AS [last_processed_at_utc]
            FROM [{_schema}].[inbox]
            WHERE [status] = 'processed'
            GROUP BY [consumer_name]
            ORDER BY [consumer_name] ASC;
            """;

        var consumers = new List<InboxConsumerDiagnosticsSnapshot>();
        await using var consumerCommand = new SqlCommand(consumerSql, connection);
        await using var consumerReader = await consumerCommand.ExecuteReaderAsync(cancellationToken);
        while (await consumerReader.ReadAsync(cancellationToken))
        {
            consumers.Add(new InboxConsumerDiagnosticsSnapshot(
                consumerReader.GetString(0),
                consumerReader.GetInt32(1),
                ReadNullableDateTimeOffset(consumerReader, 2)));
        }

        return new InboxDiagnosticsSnapshot(processedCount, consumers);
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return new DateTimeOffset(reader.GetDateTime(ordinal), TimeSpan.Zero);
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
