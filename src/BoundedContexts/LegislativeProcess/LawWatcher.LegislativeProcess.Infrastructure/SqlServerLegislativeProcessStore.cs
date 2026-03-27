using System.Text.Json;
using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.LegislativeProcess.Application;
using LawWatcher.LegislativeProcess.Domain.Processes;
using Microsoft.Data.SqlClient;

namespace LawWatcher.LegislativeProcess.Infrastructure;

public sealed class SqlServerLegislativeProcessRepository(
    IEventStore eventStore,
    string connectionString,
    string schema = "lawwatcher") : ILegislativeProcessRepository, ILegislativeProcessOutboxWriter
{
    private readonly IEventStore _eventStore = eventStore;
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<Domain.Processes.LegislativeProcess?> GetAsync(LegislativeProcessId id, CancellationToken cancellationToken)
    {
        var history = new List<IDomainEvent>();
        await foreach (var domainEvent in _eventStore.ReadStreamAsync(GetStreamId(id), cancellationToken))
        {
            history.Add(domainEvent switch
            {
                LegislativeProcessStarted started => started,
                LegislativeStageRecorded recorded => recorded,
                _ => throw new InvalidOperationException($"Unsupported legislative process domain event type '{domainEvent.GetType().Name}'.")
            });
        }

        return history.Count == 0 ? null : Domain.Processes.LegislativeProcess.Rehydrate(history);
    }

    public Task SaveAsync(Domain.Processes.LegislativeProcess process, CancellationToken cancellationToken)
    {
        return SaveAsync(process, Array.Empty<IIntegrationEvent>(), cancellationToken);
    }

    public async Task SaveAsync(
        Domain.Processes.LegislativeProcess process,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(integrationEvents);

        var pendingEvents = process.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var expectedVersion = process.Version - pendingEvents.Length;
        if (_eventStore is IEventStoreWithOutbox outboxEventStore)
        {
            await outboxEventStore.AppendAsync(
                GetStreamId(process.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                integrationEvents,
                cancellationToken);
        }
        else
        {
            await _eventStore.AppendAsync(
                GetStreamId(process.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                cancellationToken);
        }

        process.DequeueUncommittedEvents();
    }

    private static string GetStreamId(LegislativeProcessId id) => $"legislative-process:{id.Value:D}";

    private const string StreamType = "legislative-process.process";

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

public sealed class SqlServerLegislativeProcessProjectionStore(
    string connectionString,
    string schema = "lawwatcher") : ILegislativeProcessReadRepository, ILegislativeProcessProjection
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<IReadOnlyCollection<LegislativeProcessReadModel>> GetProcessesAsync(CancellationToken cancellationToken)
    {
        var processes = new List<LegislativeProcessReadModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [process_id],
                [bill_id],
                [bill_title],
                [bill_external_id],
                [current_stage_code],
                [current_stage_label],
                [last_updated_on],
                [stages_json]
            FROM [{_schema}].[legislative_processes]
            ORDER BY [last_updated_on] DESC, [bill_title] ASC;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            processes.Add(new LegislativeProcessReadModel(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                DateOnly.FromDateTime(reader.GetDateTime(6)),
                DeserializeStages(reader.GetString(7))));
        }

        return processes;
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

        var processes = await LoadProcessesAsync(connection, transaction, cancellationToken);

        foreach (var domainEvent in domainEvents)
        {
            switch (domainEvent)
            {
                case LegislativeProcessStarted started:
                    processes[started.ProcessId.Value] = LegislativeProcessProjectionRecord.From(started);
                    break;
                case LegislativeStageRecorded recorded when processes.TryGetValue(recorded.ProcessId.Value, out var existing):
                    processes[recorded.ProcessId.Value] = existing.RecordStage(recorded.StageCode, recorded.StageLabel, recorded.StageOccurredOn, recorded.OccurredAtUtc);
                    break;
            }
        }

        foreach (var process in processes.Values)
        {
            await UpsertProcessAsync(connection, transaction, process, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, LegislativeProcessProjectionRecord>> LoadProcessesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var processes = new Dictionary<Guid, LegislativeProcessProjectionRecord>();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT
                [process_id],
                [bill_id],
                [bill_title],
                [bill_external_id],
                [current_stage_code],
                [current_stage_label],
                [last_updated_on],
                [stages_json],
                [updated_at_utc]
            FROM [{_schema}].[legislative_processes];
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var record = new LegislativeProcessProjectionRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                DateOnly.FromDateTime(reader.GetDateTime(6)),
                DeserializeProjectionStages(reader.GetString(7)),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)));
            processes[record.Id] = record;
        }

        return processes;
    }

    private async Task UpsertProcessAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        LegislativeProcessProjectionRecord process,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[legislative_processes]
            SET
                [bill_id] = @billId,
                [bill_title] = @billTitle,
                [bill_external_id] = @billExternalId,
                [current_stage_code] = @currentStageCode,
                [current_stage_label] = @currentStageLabel,
                [last_updated_on] = @lastUpdatedOn,
                [stages_json] = @stagesJson,
                [updated_at_utc] = @updatedAtUtc
            WHERE [process_id] = @processId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[legislative_processes]
                (
                    [process_id],
                    [bill_id],
                    [bill_title],
                    [bill_external_id],
                    [current_stage_code],
                    [current_stage_label],
                    [last_updated_on],
                    [stages_json],
                    [updated_at_utc]
                )
                VALUES
                (
                    @processId,
                    @billId,
                    @billTitle,
                    @billExternalId,
                    @currentStageCode,
                    @currentStageLabel,
                    @lastUpdatedOn,
                    @stagesJson,
                    @updatedAtUtc
                );
            END
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@processId", process.Id);
        command.Parameters.AddWithValue("@billId", process.BillId);
        command.Parameters.AddWithValue("@billTitle", process.BillTitle);
        command.Parameters.AddWithValue("@billExternalId", process.BillExternalId);
        command.Parameters.AddWithValue("@currentStageCode", process.CurrentStageCode);
        command.Parameters.AddWithValue("@currentStageLabel", process.CurrentStageLabel);
        command.Parameters.AddWithValue("@lastUpdatedOn", process.LastUpdatedOn.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@stagesJson", JsonSerializer.Serialize(process.Stages));
        command.Parameters.AddWithValue("@updatedAtUtc", process.UpdatedAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyCollection<LegislativeStageReadModel> DeserializeStages(string stagesJson)
    {
        return (JsonSerializer.Deserialize<LegislativeStageProjectionRecord[]>(stagesJson) ?? [])
            .Select(stage => new LegislativeStageReadModel(stage.Code, stage.Label, stage.OccurredOn))
            .ToArray();
    }

    private static LegislativeStageProjectionRecord[] DeserializeProjectionStages(string stagesJson)
    {
        return JsonSerializer.Deserialize<LegislativeStageProjectionRecord[]>(stagesJson) ?? [];
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

    private sealed record LegislativeProcessProjectionRecord(
        Guid Id,
        Guid BillId,
        string BillTitle,
        string BillExternalId,
        string CurrentStageCode,
        string CurrentStageLabel,
        DateOnly LastUpdatedOn,
        LegislativeStageProjectionRecord[] Stages,
        DateTimeOffset UpdatedAtUtc)
    {
        public static LegislativeProcessProjectionRecord From(LegislativeProcessStarted started)
        {
            return new LegislativeProcessProjectionRecord(
                started.ProcessId.Value,
                started.BillId,
                started.BillTitle,
                started.BillExternalId,
                started.StageCode,
                started.StageLabel,
                started.StageOccurredOn,
                [new LegislativeStageProjectionRecord(started.StageCode, started.StageLabel, started.StageOccurredOn)],
                started.OccurredAtUtc);
        }

        public LegislativeProcessProjectionRecord RecordStage(string code, string label, DateOnly occurredOn, DateTimeOffset updatedAtUtc)
        {
            var exists = Stages.Any(stage =>
                stage.Code.Equals(code, StringComparison.OrdinalIgnoreCase) &&
                stage.Label.Equals(label, StringComparison.OrdinalIgnoreCase) &&
                stage.OccurredOn == occurredOn);

            if (exists)
            {
                return this;
            }

            var updatedStages = Stages
                .Append(new LegislativeStageProjectionRecord(code, label, occurredOn))
                .ToArray();

            var current = updatedStages
                .OrderByDescending(stage => stage.OccurredOn)
                .ThenBy(stage => stage.Code, StringComparer.OrdinalIgnoreCase)
                .First();

            return this with
            {
                CurrentStageCode = current.Code,
                CurrentStageLabel = current.Label,
                LastUpdatedOn = current.OccurredOn,
                Stages = updatedStages,
                UpdatedAtUtc = updatedAtUtc
            };
        }
    }

    private sealed record LegislativeStageProjectionRecord(
        string Code,
        string Label,
        DateOnly OccurredOn);
}
