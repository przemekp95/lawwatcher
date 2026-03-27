using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Domain.Backfills;
using Microsoft.Data.SqlClient;

namespace LawWatcher.IntegrationApi.Infrastructure;

public sealed class SqlServerBackfillRequestRepository(
    IEventStore eventStore,
    string connectionString,
    string schema = "lawwatcher") : IBackfillRequestRepository, IBackfillRequestOutboxWriter
{
    private readonly IEventStore _eventStore = eventStore;
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<BackfillRequest?> GetAsync(BackfillRequestId id, CancellationToken cancellationToken)
    {
        var history = new List<IDomainEvent>();
        await foreach (var domainEvent in _eventStore.ReadStreamAsync(GetStreamId(id), cancellationToken))
        {
            history.Add(domainEvent);
        }

        return history.Count == 0 ? null : BackfillRequest.Rehydrate(history);
    }

    public async Task<BackfillRequest?> GetNextQueuedAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT TOP (1) [backfill_request_id]
            FROM [{_schema}].[backfill_requests]
            WHERE [status] = @status
            ORDER BY [requested_at_utc] ASC, [source] ASC, [scope] ASC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@status", "queued");

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return null;
        }

        return await GetAsync(new BackfillRequestId((Guid)value), cancellationToken);
    }

    public async Task SaveAsync(BackfillRequest backfillRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backfillRequest);

        var pendingEvents = backfillRequest.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var expectedVersion = backfillRequest.Version - pendingEvents.Length;
        await _eventStore.AppendAsync(
            GetStreamId(backfillRequest.Id),
            StreamType,
            expectedVersion,
            pendingEvents,
            cancellationToken);

        backfillRequest.DequeueUncommittedEvents();
    }

    public async Task SaveAsync(
        BackfillRequest backfillRequest,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backfillRequest);
        ArgumentNullException.ThrowIfNull(integrationEvents);

        var pendingEvents = backfillRequest.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var expectedVersion = backfillRequest.Version - pendingEvents.Length;
        if (_eventStore is IEventStoreWithOutbox outboxEventStore)
        {
            await outboxEventStore.AppendAsync(
                GetStreamId(backfillRequest.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                integrationEvents,
                cancellationToken);
        }
        else
        {
            await _eventStore.AppendAsync(
                GetStreamId(backfillRequest.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                cancellationToken);
        }

        backfillRequest.DequeueUncommittedEvents();
    }

    private static string GetStreamId(BackfillRequestId id) => $"backfill:{id.Value:D}";

    private const string StreamType = "integration-api.backfill-request";

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

public sealed class SqlServerBackfillRequestProjectionStore(
    string connectionString,
    string schema = "lawwatcher") : IBackfillRequestReadRepository, IBackfillRequestProjection
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<IReadOnlyCollection<BackfillRequestReadModel>> GetBackfillsAsync(CancellationToken cancellationToken)
    {
        var backfills = new List<BackfillRequestReadModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [backfill_request_id],
                [source],
                [scope],
                [status],
                [requested_by],
                [requested_from],
                [requested_to],
                [requested_at_utc],
                [started_at_utc],
                [completed_at_utc]
            FROM [{_schema}].[backfill_requests];
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            backfills.Add(new BackfillRequestReadModel(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateOnly.FromDateTime(reader.GetDateTime(5)),
                reader.IsDBNull(6) ? null : DateOnly.FromDateTime(reader.GetDateTime(6)),
                new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero),
                reader.IsDBNull(8) ? null : new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero),
                reader.IsDBNull(9) ? null : new DateTimeOffset(reader.GetDateTime(9), TimeSpan.Zero)));
        }

        return backfills;
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
            switch (domainEvent)
            {
                case BackfillRequested requested:
                    await UpsertRequestedAsync(connection, transaction, requested, cancellationToken);
                    break;
                case BackfillStarted started:
                    await UpdateStartedAsync(connection, transaction, started, cancellationToken);
                    break;
                case BackfillCompleted completed:
                    await UpdateCompletedAsync(connection, transaction, completed, cancellationToken);
                    break;
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task UpsertRequestedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        BackfillRequested requested,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[backfill_requests]
            SET
                [source] = @source,
                [scope] = @scope,
                [status] = @status,
                [requested_by] = @requestedBy,
                [requested_from] = @requestedFrom,
                [requested_to] = @requestedTo,
                [requested_at_utc] = @requestedAtUtc,
                [started_at_utc] = NULL,
                [completed_at_utc] = NULL
            WHERE [backfill_request_id] = @backfillRequestId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[backfill_requests]
                (
                    [backfill_request_id],
                    [source],
                    [scope],
                    [status],
                    [requested_by],
                    [requested_from],
                    [requested_to],
                    [requested_at_utc],
                    [started_at_utc],
                    [completed_at_utc]
                )
                VALUES
                (
                    @backfillRequestId,
                    @source,
                    @scope,
                    @status,
                    @requestedBy,
                    @requestedFrom,
                    @requestedTo,
                    @requestedAtUtc,
                    NULL,
                    NULL
                );
            END
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@backfillRequestId", requested.BackfillRequestId.Value);
        command.Parameters.AddWithValue("@source", requested.Source);
        command.Parameters.AddWithValue("@scope", requested.Scope);
        command.Parameters.AddWithValue("@status", "queued");
        command.Parameters.AddWithValue("@requestedBy", requested.RequestedBy);
        command.Parameters.AddWithValue("@requestedFrom", requested.RequestedFrom.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@requestedTo", requested.RequestedTo?.ToDateTime(TimeOnly.MinValue) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@requestedAtUtc", requested.OccurredAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateStartedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        BackfillStarted started,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[backfill_requests]
            SET
                [status] = @status,
                [started_at_utc] = @startedAtUtc
            WHERE [backfill_request_id] = @backfillRequestId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@backfillRequestId", started.BackfillRequestId.Value);
        command.Parameters.AddWithValue("@status", "running");
        command.Parameters.AddWithValue("@startedAtUtc", started.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"Backfill projection cannot apply start event for missing backfill '{started.BackfillRequestId.Value:D}'.");
        }
    }

    private async Task UpdateCompletedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        BackfillCompleted completed,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[backfill_requests]
            SET
                [status] = @status,
                [started_at_utc] = ISNULL([started_at_utc], @completedAtUtc),
                [completed_at_utc] = @completedAtUtc
            WHERE [backfill_request_id] = @backfillRequestId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@backfillRequestId", completed.BackfillRequestId.Value);
        command.Parameters.AddWithValue("@status", "completed");
        command.Parameters.AddWithValue("@completedAtUtc", completed.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"Backfill projection cannot apply completion event for missing backfill '{completed.BackfillRequestId.Value:D}'.");
        }
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
