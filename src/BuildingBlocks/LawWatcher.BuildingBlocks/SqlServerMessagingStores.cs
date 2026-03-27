using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using LawWatcher.BuildingBlocks.Domain;
using Microsoft.Data.SqlClient;

namespace LawWatcher.BuildingBlocks.Messaging;

public sealed class SqlServerEventStore : IEventStoreWithOutbox
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly string _schema;

    public SqlServerEventStore(string connectionString, string schema = "lawwatcher")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _schema = SqlServerSchemaName.Validate(schema);
    }

    public async Task AppendAsync(
        string streamId,
        string streamType,
        long expectedVersion,
        IReadOnlyCollection<IDomainEvent> events,
        CancellationToken cancellationToken)
    {
        await AppendAsync(
            streamId,
            streamType,
            expectedVersion,
            events,
            Array.Empty<IIntegrationEvent>(),
            cancellationToken);
    }

    public async Task AppendAsync(
        string streamId,
        string streamType,
        long expectedVersion,
        IReadOnlyCollection<IDomainEvent> events,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamType);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(integrationEvents);

        if (events.Count == 0 && integrationEvents.Count == 0)
        {
            return;
        }

        await SqlServerEventOutboxAppender.AppendAsync(
            _connectionString,
            _schema,
            streamId,
            streamType,
            expectedVersion,
            events,
            integrationEvents,
            cancellationToken);
    }

    public async IAsyncEnumerable<IDomainEvent> ReadStreamAsync(
        string streamId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT [event_type], [payload], [metadata]
            FROM [{_schema}].[event_store]
            WHERE [stream_id] = @streamId
            ORDER BY [stream_version] ASC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@streamId", streamId);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var eventType = reader.GetString(0);
            var payload = reader.GetString(1);
            var metadata = reader.IsDBNull(2) ? null : reader.GetString(2);
            yield return DeserializeDomainEvent(eventType, payload, metadata);
        }
    }

    internal static async Task<long> GetCurrentVersionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string schema,
        string streamId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT ISNULL(MAX([stream_version]), 0)
            FROM [{schema}].[event_store]
            WHERE [stream_id] = @streamId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@streamId", streamId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar);
    }

    internal static async Task InsertEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string schema,
        string streamId,
        string streamType,
        long streamVersion,
        IDomainEvent domainEvent,
        CancellationToken cancellationToken)
    {
        var eventClrType = domainEvent.GetType();
        var eventType = eventClrType.FullName ?? eventClrType.Name;
        var payload = JsonSerializer.Serialize(domainEvent, eventClrType, SerializerOptions);
        var metadata = JsonSerializer.Serialize(new SqlMessageMetadata(eventClrType.AssemblyQualifiedName), SerializerOptions);

        var sql = $"""
            INSERT INTO [{schema}].[event_store]
            (
                [event_id],
                [stream_id],
                [stream_type],
                [stream_version],
                [event_type],
                [event_schema_version],
                [payload],
                [metadata],
                [occurred_at_utc]
            )
            VALUES
            (
                @eventId,
                @streamId,
                @streamType,
                @streamVersion,
                @eventType,
                @eventSchemaVersion,
                @payload,
                @metadata,
                @occurredAtUtc
            );
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@eventId", domainEvent.EventId);
        command.Parameters.AddWithValue("@streamId", streamId);
        command.Parameters.AddWithValue("@streamType", streamType);
        command.Parameters.AddWithValue("@streamVersion", streamVersion);
        command.Parameters.AddWithValue("@eventType", eventType);
        command.Parameters.AddWithValue("@eventSchemaVersion", 1);
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@metadata", metadata);
        command.Parameters.AddWithValue("@occurredAtUtc", domainEvent.OccurredAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task InsertOutboxMessageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string schema,
        IIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        var messageClrType = integrationEvent.GetType();
        var messageType = messageClrType.FullName ?? messageClrType.Name;
        var payload = JsonSerializer.Serialize(integrationEvent, messageClrType, SerializerOptions);
        var metadata = JsonSerializer.Serialize(new SqlMessageMetadata(messageClrType.AssemblyQualifiedName), SerializerOptions);

        var sql = $"""
            INSERT INTO [{schema}].[outbox]
            (
                [outbox_message_id],
                [message_type],
                [message_version],
                [payload],
                [metadata],
                [status],
                [next_attempt_at_utc],
                [published_at_utc]
            )
            VALUES
            (
                @messageId,
                @messageType,
                @messageVersion,
                @payload,
                @metadata,
                @status,
                NULL,
                NULL
            );
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@messageId", integrationEvent.EventId);
        command.Parameters.AddWithValue("@messageType", messageType);
        command.Parameters.AddWithValue("@messageVersion", 1);
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@metadata", metadata);
        command.Parameters.AddWithValue("@status", "pending");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IDomainEvent DeserializeDomainEvent(string eventType, string payload, string? metadataJson)
    {
        var resolvedType = ResolveClrType(eventType, metadataJson);
        var domainEvent = JsonSerializer.Deserialize(payload, resolvedType, SerializerOptions) as IDomainEvent;
        return domainEvent
            ?? throw new InvalidOperationException($"Unable to deserialize domain event '{eventType}' to '{resolvedType.FullName}'.");
    }

    private static Type ResolveClrType(string fallbackTypeName, string? metadataJson)
    {
        var metadata = string.IsNullOrWhiteSpace(metadataJson)
            ? null
            : JsonSerializer.Deserialize<SqlMessageMetadata>(metadataJson, SerializerOptions);

        foreach (var candidate in new[] { metadata?.ClrType, fallbackTypeName })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var resolved = Type.GetType(candidate, throwOnError: false);
            if (resolved is not null)
            {
                return resolved;
            }

            resolved = AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(assembly => assembly.GetType(candidate, throwOnError: false, ignoreCase: false))
                .FirstOrDefault(type => type is not null);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        throw new InvalidOperationException($"Unable to resolve CLR type for event '{fallbackTypeName}'.");
    }
}

public sealed class SqlServerOutboxStore : IOutboxStore, IOutboxMessageStore
{
    private readonly string _connectionString;
    private readonly string _schema;

    public SqlServerOutboxStore(string connectionString, string schema = "lawwatcher")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _schema = SqlServerSchemaName.Validate(schema);
    }

    public bool SupportsPolling => true;

    public async Task EnqueueAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await SqlServerEventStore.InsertOutboxMessageAsync(connection, transaction, _schema, integrationEvent, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<OutboxMessage>> GetPendingAsync(
        IReadOnlyCollection<string> messageTypes,
        int maxCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageTypes);
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "Maximum batch size must be greater than zero.");
        }

        var normalizedMessageTypes = messageTypes
            .Where(messageType => !string.IsNullOrWhiteSpace(messageType))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedMessageTypes.Length == 0)
        {
            return Array.Empty<OutboxMessage>();
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var parameters = new List<string>(normalizedMessageTypes.Length);
        for (var index = 0; index < normalizedMessageTypes.Length; index++)
        {
            parameters.Add($"@messageType{index}");
        }

        var sql = $"""
            SELECT TOP (@maxCount)
                [outbox_message_id],
                [message_type],
                [payload],
                [metadata],
                [attempt_count],
                [created_at_utc],
                [next_attempt_at_utc]
            FROM [{_schema}].[outbox]
            WHERE [status] = 'pending'
              AND ([next_attempt_at_utc] IS NULL OR [next_attempt_at_utc] <= SYSUTCDATETIME())
              AND [message_type] IN ({string.Join(", ", parameters)})
            ORDER BY
                COALESCE([next_attempt_at_utc], [created_at_utc]) ASC,
                [created_at_utc] ASC,
                [outbox_message_id] ASC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@maxCount", maxCount);
        for (var index = 0; index < normalizedMessageTypes.Length; index++)
        {
            command.Parameters.AddWithValue(parameters[index], normalizedMessageTypes[index]);
        }

        var messages = new List<OutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new OutboxMessage(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero),
                reader.IsDBNull(6) ? null : new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero)));
        }

        return messages;
    }

    public async Task MarkPublishedAsync(Guid messageId, DateTimeOffset publishedAtUtc, CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[outbox]
            SET [status] = 'published',
                [published_at_utc] = @publishedAtUtc,
                [next_attempt_at_utc] = NULL
            WHERE [outbox_message_id] = @messageId;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@messageId", messageId);
        command.Parameters.AddWithValue("@publishedAtUtc", publishedAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeferAsync(Guid messageId, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[outbox]
            SET [status] = 'pending',
                [attempt_count] = [attempt_count] + 1,
                [next_attempt_at_utc] = @nextAttemptAtUtc
            WHERE [outbox_message_id] = @messageId;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@messageId", messageId);
        command.Parameters.AddWithValue("@nextAttemptAtUtc", nextAttemptAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal static class SqlServerEventOutboxAppender
{
    public static async Task AppendAsync(
        string connectionString,
        string schema,
        string streamId,
        string streamType,
        long expectedVersion,
        IReadOnlyCollection<IDomainEvent> events,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var currentVersion = await SqlServerEventStore.GetCurrentVersionAsync(connection, transaction, schema, streamId, cancellationToken);
        if (currentVersion != expectedVersion)
        {
            throw new EventStreamConcurrencyException(streamId, expectedVersion, currentVersion);
        }

        var nextVersion = currentVersion;
        foreach (var domainEvent in events)
        {
            nextVersion++;
            await SqlServerEventStore.InsertEventAsync(
                connection,
                transaction,
                schema,
                streamId,
                streamType,
                nextVersion,
                domainEvent,
                cancellationToken);
        }

        foreach (var integrationEvent in integrationEvents)
        {
            await SqlServerEventStore.InsertOutboxMessageAsync(
                connection,
                transaction,
                schema,
                integrationEvent,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed class SqlServerInboxStore : IInboxStore
{
    private readonly string _connectionString;
    private readonly string _schema;

    public SqlServerInboxStore(string connectionString, string schema = "lawwatcher")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _schema = SqlServerSchemaName.Validate(schema);
    }

    public async Task<bool> HasProcessedAsync(Guid messageId, string consumerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);

        var sql = $"""
            SELECT CASE WHEN EXISTS
            (
                SELECT 1
                FROM [{_schema}].[inbox]
                WHERE [message_id] = @messageId
                  AND [consumer_name] = @consumerName
                  AND [status] = 'processed'
            )
            THEN 1 ELSE 0 END;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@messageId", messageId);
        command.Parameters.AddWithValue("@consumerName", consumerName);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar) == 1;
    }

    public async Task MarkProcessedAsync(Guid messageId, string consumerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);

        var sql = $"""
            UPDATE [{_schema}].[inbox]
            SET [status] = 'processed',
                [processed_at_utc] = COALESCE([processed_at_utc], SYSUTCDATETIME())
            WHERE [message_id] = @messageId
              AND [consumer_name] = @consumerName;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[inbox]
                (
                    [message_id],
                    [consumer_name],
                    [processed_at_utc],
                    [status],
                    [metadata]
                )
                VALUES
                (
                    @messageId,
                    @consumerName,
                    SYSUTCDATETIME(),
                    'processed',
                    NULL
                );
            END
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@messageId", messageId);
        command.Parameters.AddWithValue("@consumerName", consumerName);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

file sealed record SqlMessageMetadata(string? ClrType);

file static class SqlServerSchemaName
{
    public static string Validate(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (!Regex.IsMatch(schema, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw new ArgumentOutOfRangeException(nameof(schema), "SQL schema name contains unsupported characters.");
        }

        return schema;
    }
}
