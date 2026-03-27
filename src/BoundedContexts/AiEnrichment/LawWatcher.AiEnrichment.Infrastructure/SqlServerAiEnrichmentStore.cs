using System.Text.Json;
using LawWatcher.AiEnrichment.Application;
using LawWatcher.AiEnrichment.Domain.Tasks;
using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using Microsoft.Data.SqlClient;

namespace LawWatcher.AiEnrichment.Infrastructure;

public sealed class SqlServerAiEnrichmentTaskRepository(
    IEventStore eventStore,
    string connectionString,
    string schema = "lawwatcher") : IAiEnrichmentTaskRepository, IAiEnrichmentTaskOutboxWriter
{
    private readonly IEventStore _eventStore = eventStore;
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<AiEnrichmentTask?> GetAsync(AiEnrichmentTaskId id, CancellationToken cancellationToken)
    {
        var history = new List<IDomainEvent>();
        await foreach (var domainEvent in _eventStore.ReadStreamAsync(GetStreamId(id), cancellationToken))
        {
            history.Add(domainEvent);
        }

        return history.Count == 0 ? null : AiEnrichmentTask.Rehydrate(history);
    }

    public async Task<AiEnrichmentTask?> GetNextQueuedAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT TOP (1) [task_id]
            FROM [{_schema}].[ai_enrichment_tasks]
            WHERE [status] = @status
            ORDER BY [requested_at_utc] ASC, [subject_title] ASC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@status", "queued");

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return null;
        }

        return await GetAsync(new AiEnrichmentTaskId((Guid)value), cancellationToken);
    }

    public async Task SaveAsync(AiEnrichmentTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        var pendingEvents = task.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var expectedVersion = task.Version - pendingEvents.Length;
        await _eventStore.AppendAsync(
            GetStreamId(task.Id),
            StreamType,
            expectedVersion,
            pendingEvents,
            cancellationToken);

        task.DequeueUncommittedEvents();
    }

    public async Task SaveAsync(
        AiEnrichmentTask task,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(integrationEvents);

        var pendingEvents = task.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var expectedVersion = task.Version - pendingEvents.Length;
        if (_eventStore is IEventStoreWithOutbox outboxEventStore)
        {
            await outboxEventStore.AppendAsync(
                GetStreamId(task.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                integrationEvents,
                cancellationToken);
        }
        else
        {
            await _eventStore.AppendAsync(
                GetStreamId(task.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                cancellationToken);
        }

        task.DequeueUncommittedEvents();
    }

    private static string GetStreamId(AiEnrichmentTaskId id) => $"ai-task:{id.Value:D}";

    private const string StreamType = "ai-enrichment.task";

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

public sealed class SqlServerAiEnrichmentTaskProjectionStore(
    string connectionString,
    string schema = "lawwatcher") : IAiEnrichmentTaskReadRepository, IAiEnrichmentTaskProjection
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<IReadOnlyCollection<AiEnrichmentTaskReadModel>> GetTasksAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<AiEnrichmentTaskReadModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [task_id],
                [kind],
                [subject_type],
                [subject_id],
                [subject_title],
                [status],
                [model],
                [content],
                [error],
                [citations_json],
                [requested_at_utc],
                [started_at_utc],
                [completed_at_utc],
                [failed_at_utc]
            FROM [{_schema}].[ai_enrichment_tasks];
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tasks.Add(new AiEnrichmentTaskReadModel(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                DeserializeCitations(reader.GetString(9)),
                new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero),
                reader.IsDBNull(11) ? null : new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero),
                reader.IsDBNull(12) ? null : new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
                reader.IsDBNull(13) ? null : new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero)));
        }

        return tasks;
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
                case AiEnrichmentRequested requested:
                    await UpsertRequestedAsync(connection, transaction, requested, cancellationToken);
                    break;
                case AiEnrichmentProcessingStarted started:
                    await UpdateStartedAsync(connection, transaction, started, cancellationToken);
                    break;
                case AiEnrichmentCompleted completed:
                    await UpdateCompletedAsync(connection, transaction, completed, cancellationToken);
                    break;
                case AiEnrichmentFailed failed:
                    await UpdateFailedAsync(connection, transaction, failed, cancellationToken);
                    break;
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task UpsertRequestedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AiEnrichmentRequested requested,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[ai_enrichment_tasks]
            SET
                [kind] = @kind,
                [subject_type] = @subjectType,
                [subject_id] = @subjectId,
                [subject_title] = @subjectTitle,
                [status] = @status,
                [model] = NULL,
                [content] = NULL,
                [error] = NULL,
                [citations_json] = @citationsJson,
                [requested_at_utc] = @requestedAtUtc,
                [started_at_utc] = NULL,
                [completed_at_utc] = NULL,
                [failed_at_utc] = NULL
            WHERE [task_id] = @taskId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[ai_enrichment_tasks]
                (
                    [task_id],
                    [kind],
                    [subject_type],
                    [subject_id],
                    [subject_title],
                    [status],
                    [model],
                    [content],
                    [error],
                    [citations_json],
                    [requested_at_utc],
                    [started_at_utc],
                    [completed_at_utc],
                    [failed_at_utc]
                )
                VALUES
                (
                    @taskId,
                    @kind,
                    @subjectType,
                    @subjectId,
                    @subjectTitle,
                    @status,
                    NULL,
                    NULL,
                    NULL,
                    @citationsJson,
                    @requestedAtUtc,
                    NULL,
                    NULL,
                    NULL
                );
            END
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@taskId", requested.TaskId.Value);
        command.Parameters.AddWithValue("@kind", requested.Kind);
        command.Parameters.AddWithValue("@subjectType", requested.SubjectType);
        command.Parameters.AddWithValue("@subjectId", requested.SubjectId);
        command.Parameters.AddWithValue("@subjectTitle", requested.SubjectTitle);
        command.Parameters.AddWithValue("@status", "queued");
        command.Parameters.AddWithValue("@citationsJson", "[]");
        command.Parameters.AddWithValue("@requestedAtUtc", requested.OccurredAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateStartedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AiEnrichmentProcessingStarted started,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[ai_enrichment_tasks]
            SET
                [status] = @status,
                [started_at_utc] = @startedAtUtc,
                [failed_at_utc] = NULL
            WHERE [task_id] = @taskId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@taskId", started.TaskId.Value);
        command.Parameters.AddWithValue("@status", "running");
        command.Parameters.AddWithValue("@startedAtUtc", started.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"AI projection cannot apply start event for missing task '{started.TaskId.Value:D}'.");
        }
    }

    private async Task UpdateCompletedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AiEnrichmentCompleted completed,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[ai_enrichment_tasks]
            SET
                [status] = @status,
                [model] = @model,
                [content] = @content,
                [error] = NULL,
                [citations_json] = @citationsJson,
                [started_at_utc] = ISNULL([started_at_utc], @completedAtUtc),
                [completed_at_utc] = @completedAtUtc,
                [failed_at_utc] = NULL
            WHERE [task_id] = @taskId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@taskId", completed.TaskId.Value);
        command.Parameters.AddWithValue("@status", "completed");
        command.Parameters.AddWithValue("@model", completed.Model);
        command.Parameters.AddWithValue("@content", completed.Content);
        command.Parameters.AddWithValue("@citationsJson", JsonSerializer.Serialize(completed.Citations));
        command.Parameters.AddWithValue("@completedAtUtc", completed.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"AI projection cannot apply completion event for missing task '{completed.TaskId.Value:D}'.");
        }
    }

    private async Task UpdateFailedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AiEnrichmentFailed failed,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[ai_enrichment_tasks]
            SET
                [status] = @status,
                [error] = @error,
                [started_at_utc] = ISNULL([started_at_utc], @failedAtUtc),
                [completed_at_utc] = NULL,
                [failed_at_utc] = @failedAtUtc
            WHERE [task_id] = @taskId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@taskId", failed.TaskId.Value);
        command.Parameters.AddWithValue("@status", "failed");
        command.Parameters.AddWithValue("@error", failed.Error);
        command.Parameters.AddWithValue("@failedAtUtc", failed.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"AI projection cannot apply failure event for missing task '{failed.TaskId.Value:D}'.");
        }
    }

    private static IReadOnlyCollection<string> DeserializeCitations(string citationsJson)
    {
        return JsonSerializer.Deserialize<string[]>(citationsJson) ?? [];
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
