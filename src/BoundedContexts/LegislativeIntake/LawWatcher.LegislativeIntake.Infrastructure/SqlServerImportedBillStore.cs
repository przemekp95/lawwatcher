using System.Text.Json;
using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.LegislativeIntake.Application;
using LawWatcher.LegislativeIntake.Domain.Bills;
using Microsoft.Data.SqlClient;

namespace LawWatcher.LegislativeIntake.Infrastructure;

public sealed class SqlServerImportedBillRepository(
    IEventStore eventStore,
    string connectionString,
    string schema = "lawwatcher") : IImportedBillRepository, IImportedBillOutboxWriter
{
    private readonly IEventStore _eventStore = eventStore;
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<ImportedBill?> GetAsync(BillId id, CancellationToken cancellationToken)
    {
        var history = new List<IDomainEvent>();
        await foreach (var domainEvent in _eventStore.ReadStreamAsync(GetStreamId(id), cancellationToken))
        {
            history.Add(domainEvent switch
            {
                BillImported imported => imported,
                BillDocumentAttached attached => attached,
                _ => throw new InvalidOperationException($"Unsupported imported bill domain event type '{domainEvent.GetType().Name}'.")
            });
        }

        return history.Count == 0 ? null : ImportedBill.Rehydrate(history);
    }

    public Task SaveAsync(ImportedBill bill, CancellationToken cancellationToken)
    {
        return SaveAsync(bill, Array.Empty<IIntegrationEvent>(), cancellationToken);
    }

    public async Task SaveAsync(
        ImportedBill bill,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(integrationEvents);

        var pendingEvents = bill.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var expectedVersion = bill.Version - pendingEvents.Length;
        if (_eventStore is IEventStoreWithOutbox outboxEventStore)
        {
            await outboxEventStore.AppendAsync(
                GetStreamId(bill.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                integrationEvents,
                cancellationToken);
        }
        else
        {
            await _eventStore.AppendAsync(
                GetStreamId(bill.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                cancellationToken);
        }

        bill.DequeueUncommittedEvents();
    }

    private static string GetStreamId(BillId id) => $"bill:{id.Value:D}";

    private const string StreamType = "legislative-intake.imported-bill";

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

public sealed class SqlServerImportedBillProjectionStore(
    string connectionString,
    string schema = "lawwatcher") : IImportedBillReadRepository, IImportedBillProjection
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<IReadOnlyCollection<ImportedBillReadModel>> GetBillsAsync(CancellationToken cancellationToken)
    {
        var bills = new List<ImportedBillReadModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [bill_id],
                [source_system],
                [external_id],
                [title],
                [source_url],
                [submitted_on],
                [document_kinds_json]
            FROM [{_schema}].[imported_bills]
            ORDER BY [submitted_on] DESC, [title] ASC;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bills.Add(new ImportedBillReadModel(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateOnly.FromDateTime(reader.GetDateTime(5)),
                DeserializeDocumentKinds(reader.GetString(6))));
        }

        return bills;
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

        var bills = await LoadBillsAsync(connection, transaction, cancellationToken);

        foreach (var domainEvent in domainEvents)
        {
            switch (domainEvent)
            {
                case BillImported imported:
                    bills[imported.BillId.Value] = new ImportedBillProjectionRecord(
                        imported.BillId.Value,
                        imported.SourceSystem,
                        imported.ExternalId,
                        imported.Title,
                        imported.SourceUrl,
                        imported.SubmittedOn,
                        [],
                        imported.OccurredAtUtc);
                    break;
                case BillDocumentAttached attached when bills.TryGetValue(attached.BillId.Value, out var existing):
                    bills[attached.BillId.Value] = existing with
                    {
                        DocumentKinds = existing.DocumentKinds
                            .Append(attached.Kind)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        UpdatedAtUtc = attached.OccurredAtUtc
                    };
                    break;
            }
        }

        foreach (var bill in bills.Values)
        {
            await UpsertBillAsync(connection, transaction, bill, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, ImportedBillProjectionRecord>> LoadBillsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var bills = new Dictionary<Guid, ImportedBillProjectionRecord>();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT
                [bill_id],
                [source_system],
                [external_id],
                [title],
                [source_url],
                [submitted_on],
                [document_kinds_json],
                [updated_at_utc]
            FROM [{_schema}].[imported_bills];
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var record = new ImportedBillProjectionRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateOnly.FromDateTime(reader.GetDateTime(5)),
                DeserializeDocumentKinds(reader.GetString(6)).ToArray(),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)));
            bills[record.Id] = record;
        }

        return bills;
    }

    private async Task UpsertBillAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ImportedBillProjectionRecord bill,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[imported_bills]
            SET
                [source_system] = @sourceSystem,
                [external_id] = @externalId,
                [title] = @title,
                [source_url] = @sourceUrl,
                [submitted_on] = @submittedOn,
                [document_kinds_json] = @documentKindsJson,
                [updated_at_utc] = @updatedAtUtc
            WHERE [bill_id] = @billId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[imported_bills]
                (
                    [bill_id],
                    [source_system],
                    [external_id],
                    [title],
                    [source_url],
                    [submitted_on],
                    [document_kinds_json],
                    [updated_at_utc]
                )
                VALUES
                (
                    @billId,
                    @sourceSystem,
                    @externalId,
                    @title,
                    @sourceUrl,
                    @submittedOn,
                    @documentKindsJson,
                    @updatedAtUtc
                );
            END
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@billId", bill.Id);
        command.Parameters.AddWithValue("@sourceSystem", bill.SourceSystem);
        command.Parameters.AddWithValue("@externalId", bill.ExternalId);
        command.Parameters.AddWithValue("@title", bill.Title);
        command.Parameters.AddWithValue("@sourceUrl", bill.SourceUrl);
        command.Parameters.AddWithValue("@submittedOn", bill.SubmittedOn.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@documentKindsJson", JsonSerializer.Serialize(bill.DocumentKinds));
        command.Parameters.AddWithValue("@updatedAtUtc", bill.UpdatedAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyCollection<string> DeserializeDocumentKinds(string documentKindsJson)
    {
        return JsonSerializer.Deserialize<string[]>(documentKindsJson) ?? [];
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

    private sealed record ImportedBillProjectionRecord(
        Guid Id,
        string SourceSystem,
        string ExternalId,
        string Title,
        string SourceUrl,
        DateOnly SubmittedOn,
        string[] DocumentKinds,
        DateTimeOffset UpdatedAtUtc);
}
