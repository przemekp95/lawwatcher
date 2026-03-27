using System.Data;
using System.Text.Json;
using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.Notifications.Application;
using LawWatcher.Notifications.Domain.BillAlerts;
using Microsoft.Data.SqlClient;

namespace LawWatcher.Notifications.Infrastructure;

public sealed class SqlServerBillAlertRepository(
    string connectionString,
    string schema = "lawwatcher") : IBillAlertRepository, IBillAlertOutboxWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<bool> ExistsAsync(Guid profileId, Guid billId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT CASE WHEN EXISTS
            (
                SELECT 1
                FROM [{_schema}].[bill_alert_pairs]
                WHERE [profile_id] = @profileId
                  AND [bill_id] = @billId
            )
            THEN 1 ELSE 0 END;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@profileId", profileId);
        command.Parameters.AddWithValue("@billId", billId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar) == 1;
    }

    public Task SaveAsync(BillAlert alert, CancellationToken cancellationToken)
    {
        return SaveAsync(alert, Array.Empty<IIntegrationEvent>(), cancellationToken);
    }

    public async Task SaveAsync(
        BillAlert alert,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(integrationEvents);

        var pendingEvents = alert.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var streamId = GetStreamId(alert.Id);
        var expectedVersion = alert.Version - pendingEvents.Length;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var currentVersion = await GetCurrentVersionAsync(connection, transaction, streamId, cancellationToken);
        if (currentVersion != expectedVersion)
        {
            throw new EventStreamConcurrencyException(streamId, expectedVersion, currentVersion);
        }

        await ReservePairAsync(connection, transaction, alert.Id.Value, alert.ProfileId, alert.BillId, pendingEvents[0].OccurredAtUtc.UtcDateTime, cancellationToken);

        var nextVersion = currentVersion;
        foreach (var domainEvent in pendingEvents)
        {
            nextVersion++;
            await InsertEventAsync(connection, transaction, streamId, StreamType, nextVersion, domainEvent, cancellationToken);
        }

        foreach (var integrationEvent in integrationEvents)
        {
            await InsertOutboxMessageAsync(connection, transaction, integrationEvent, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        alert.DequeueUncommittedEvents();
    }

    private async Task ReservePairAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid alertId,
        Guid profileId,
        Guid billId,
        DateTime createdAtUtc,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO [{_schema}].[bill_alert_pairs]
            (
                [alert_id],
                [profile_id],
                [bill_id],
                [created_at_utc]
            )
            VALUES
            (
                @alertId,
                @profileId,
                @billId,
                @createdAtUtc
            );
            """;

        try
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@alertId", alertId);
            command.Parameters.AddWithValue("@profileId", profileId);
            command.Parameters.AddWithValue("@billId", billId);
            command.Parameters.AddWithValue("@createdAtUtc", createdAtUtc);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw new InvalidOperationException(
                $"Alert for pair '{profileId:D}:{billId:D}' already exists.",
                exception);
        }
    }

    private async Task<long> GetCurrentVersionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string streamId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT ISNULL(MAX([stream_version]), 0)
            FROM [{_schema}].[event_store]
            WHERE [stream_id] = @streamId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@streamId", streamId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(scalar);
    }

    private async Task InsertEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
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
            INSERT INTO [{_schema}].[event_store]
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

    private async Task InsertOutboxMessageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        var messageClrType = integrationEvent.GetType();
        var messageType = messageClrType.FullName ?? messageClrType.Name;
        var payload = JsonSerializer.Serialize(integrationEvent, messageClrType, SerializerOptions);
        var metadata = JsonSerializer.Serialize(new SqlMessageMetadata(messageClrType.AssemblyQualifiedName), SerializerOptions);

        var sql = $"""
            INSERT INTO [{_schema}].[outbox]
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

    private static string GetStreamId(AlertId id) => $"bill-alert:{id.Value:D}";

    private const string StreamType = "notifications.bill-alert";

    private static string ValidateSchema(string schema)
    {
        var normalized = schema.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Schema cannot be empty.", nameof(schema));
        }

        return normalized;
    }

    private sealed record SqlMessageMetadata(string? ClrType);
}

public sealed class SqlServerBillAlertProjectionStore(
    string connectionString,
    string schema = "lawwatcher") : IBillAlertReadRepository, IBillAlertProjection
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<IReadOnlyCollection<BillAlertReadModel>> GetAlertsAsync(CancellationToken cancellationToken)
    {
        var alerts = new List<BillAlertReadModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [alert_id],
                [profile_id],
                [profile_name],
                [bill_id],
                [bill_title],
                [bill_external_id],
                [bill_submitted_on],
                [alert_policy],
                [matched_keywords_json],
                [created_at_utc]
            FROM [{_schema}].[bill_alerts];
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            alerts.Add(new BillAlertReadModel(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetString(5),
                DateOnly.FromDateTime(reader.GetDateTime(6)),
                reader.GetString(7),
                DeserializeKeywords(reader.GetString(8)),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc))));
        }

        return alerts;
    }

    public async Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);
        if (domainEvents.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var domainEvent in domainEvents)
        {
            if (domainEvent is BillAlertCreated created)
            {
                await UpsertAlertAsync(connection, transaction, created, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task UpsertAlertAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        BillAlertCreated created,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[bill_alerts]
            SET
                [profile_id] = @profileId,
                [profile_name] = @profileName,
                [bill_id] = @billId,
                [bill_title] = @billTitle,
                [bill_external_id] = @billExternalId,
                [bill_submitted_on] = @billSubmittedOn,
                [alert_policy] = @alertPolicy,
                [matched_keywords_json] = @matchedKeywordsJson,
                [created_at_utc] = @createdAtUtc
            WHERE [alert_id] = @alertId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[bill_alerts]
                (
                    [alert_id],
                    [profile_id],
                    [profile_name],
                    [bill_id],
                    [bill_title],
                    [bill_external_id],
                    [bill_submitted_on],
                    [alert_policy],
                    [matched_keywords_json],
                    [created_at_utc]
                )
                VALUES
                (
                    @alertId,
                    @profileId,
                    @profileName,
                    @billId,
                    @billTitle,
                    @billExternalId,
                    @billSubmittedOn,
                    @alertPolicy,
                    @matchedKeywordsJson,
                    @createdAtUtc
                );
            END
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@alertId", created.AlertId.Value);
        command.Parameters.AddWithValue("@profileId", created.ProfileId);
        command.Parameters.AddWithValue("@profileName", created.ProfileName);
        command.Parameters.AddWithValue("@billId", created.BillId);
        command.Parameters.AddWithValue("@billTitle", created.BillTitle);
        command.Parameters.AddWithValue("@billExternalId", created.BillExternalId);
        command.Parameters.AddWithValue("@billSubmittedOn", created.BillSubmittedOn.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@alertPolicy", created.AlertPolicy);
        command.Parameters.AddWithValue("@matchedKeywordsJson", JsonSerializer.Serialize(
            created.MatchedKeywords
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(keyword => keyword, StringComparer.OrdinalIgnoreCase)
                .ToArray()));
        command.Parameters.AddWithValue("@createdAtUtc", created.OccurredAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyCollection<string> DeserializeKeywords(string keywordsJson)
    {
        return JsonSerializer.Deserialize<string[]>(keywordsJson) ?? [];
    }

    private static string ValidateSchema(string schema)
    {
        var normalized = schema.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Schema cannot be empty.", nameof(schema));
        }

        return normalized;
    }
}
