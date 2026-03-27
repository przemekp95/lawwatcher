using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Domain.Replays;
using Microsoft.Data.SqlClient;

namespace LawWatcher.IntegrationApi.Infrastructure;

public sealed class SqlServerReplayRequestRepository(
    IEventStore eventStore,
    string connectionString,
    string schema = "lawwatcher") : IReplayRequestRepository, IReplayRequestOutboxWriter
{
    private readonly IEventStore _eventStore = eventStore;
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<ReplayRequest?> GetAsync(ReplayRequestId id, CancellationToken cancellationToken)
    {
        var history = new List<IDomainEvent>();
        await foreach (var domainEvent in _eventStore.ReadStreamAsync(GetStreamId(id), cancellationToken))
        {
            history.Add(domainEvent);
        }

        return history.Count == 0 ? null : ReplayRequest.Rehydrate(history);
    }

    public async Task<ReplayRequest?> GetNextQueuedAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT TOP (1) [replay_request_id]
            FROM [{_schema}].[replay_requests]
            WHERE [status] = @status
            ORDER BY [requested_at_utc] ASC, [scope] ASC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@status", "queued");

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return null;
        }

        return await GetAsync(new ReplayRequestId((Guid)value), cancellationToken);
    }

    public async Task SaveAsync(ReplayRequest replayRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replayRequest);

        var pendingEvents = replayRequest.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var expectedVersion = replayRequest.Version - pendingEvents.Length;
        await _eventStore.AppendAsync(
            GetStreamId(replayRequest.Id),
            StreamType,
            expectedVersion,
            pendingEvents,
            cancellationToken);

        replayRequest.DequeueUncommittedEvents();
    }

    public async Task SaveAsync(
        ReplayRequest replayRequest,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replayRequest);
        ArgumentNullException.ThrowIfNull(integrationEvents);

        var pendingEvents = replayRequest.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var expectedVersion = replayRequest.Version - pendingEvents.Length;
        if (_eventStore is IEventStoreWithOutbox outboxEventStore)
        {
            await outboxEventStore.AppendAsync(
                GetStreamId(replayRequest.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                integrationEvents,
                cancellationToken);
        }
        else
        {
            await _eventStore.AppendAsync(
                GetStreamId(replayRequest.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                cancellationToken);
        }

        replayRequest.DequeueUncommittedEvents();
    }

    private static string GetStreamId(ReplayRequestId id) => $"replay:{id.Value:D}";

    private const string StreamType = "integration-api.replay-request";

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

public sealed class SqlServerReplayRequestProjectionStore(
    string connectionString,
    string schema = "lawwatcher") : IReplayRequestReadRepository, IReplayRequestProjection
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<IReadOnlyCollection<ReplayRequestReadModel>> GetReplaysAsync(CancellationToken cancellationToken)
    {
        var replays = new List<ReplayRequestReadModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [replay_request_id],
                [scope],
                [status],
                [requested_by],
                [requested_at_utc],
                [started_at_utc],
                [completed_at_utc]
            FROM [{_schema}].[replay_requests];
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            replays.Add(new ReplayRequestReadModel(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero),
                reader.IsDBNull(5) ? null : new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero),
                reader.IsDBNull(6) ? null : new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero)));
        }

        return replays;
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
                case ReplayRequested requested:
                    await UpsertRequestedAsync(connection, transaction, requested, cancellationToken);
                    break;
                case ReplayStarted started:
                    await UpdateStartedAsync(connection, transaction, started, cancellationToken);
                    break;
                case ReplayCompleted completed:
                    await UpdateCompletedAsync(connection, transaction, completed, cancellationToken);
                    break;
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task UpsertRequestedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ReplayRequested requested,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[replay_requests]
            SET
                [scope] = @scope,
                [status] = @status,
                [requested_by] = @requestedBy,
                [requested_at_utc] = @requestedAtUtc,
                [started_at_utc] = NULL,
                [completed_at_utc] = NULL
            WHERE [replay_request_id] = @replayRequestId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[replay_requests]
                (
                    [replay_request_id],
                    [scope],
                    [status],
                    [requested_by],
                    [requested_at_utc],
                    [started_at_utc],
                    [completed_at_utc]
                )
                VALUES
                (
                    @replayRequestId,
                    @scope,
                    @status,
                    @requestedBy,
                    @requestedAtUtc,
                    NULL,
                    NULL
                );
            END
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@replayRequestId", requested.ReplayRequestId.Value);
        command.Parameters.AddWithValue("@scope", requested.Scope);
        command.Parameters.AddWithValue("@status", "queued");
        command.Parameters.AddWithValue("@requestedBy", requested.RequestedBy);
        command.Parameters.AddWithValue("@requestedAtUtc", requested.OccurredAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateStartedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ReplayStarted started,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[replay_requests]
            SET
                [status] = @status,
                [started_at_utc] = @startedAtUtc
            WHERE [replay_request_id] = @replayRequestId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@replayRequestId", started.ReplayRequestId.Value);
        command.Parameters.AddWithValue("@status", "running");
        command.Parameters.AddWithValue("@startedAtUtc", started.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"Replay projection cannot apply start event for missing replay '{started.ReplayRequestId.Value:D}'.");
        }
    }

    private async Task UpdateCompletedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ReplayCompleted completed,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[replay_requests]
            SET
                [status] = @status,
                [started_at_utc] = ISNULL([started_at_utc], @completedAtUtc),
                [completed_at_utc] = @completedAtUtc
            WHERE [replay_request_id] = @replayRequestId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@replayRequestId", completed.ReplayRequestId.Value);
        command.Parameters.AddWithValue("@status", "completed");
        command.Parameters.AddWithValue("@completedAtUtc", completed.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"Replay projection cannot apply completion event for missing replay '{completed.ReplayRequestId.Value:D}'.");
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
