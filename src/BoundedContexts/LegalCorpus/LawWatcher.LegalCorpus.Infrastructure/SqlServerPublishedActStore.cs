using System.Text.Json;
using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegalCorpus.Domain.Acts;
using Microsoft.Data.SqlClient;

namespace LawWatcher.LegalCorpus.Infrastructure;

public sealed class SqlServerPublishedActRepository(
    IEventStore eventStore,
    string connectionString,
    string schema = "lawwatcher") : IPublishedActRepository, IPublishedActOutboxWriter
{
    private readonly IEventStore _eventStore = eventStore;
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<PublishedAct?> GetAsync(ActId id, CancellationToken cancellationToken)
    {
        var history = new List<IDomainEvent>();
        await foreach (var domainEvent in _eventStore.ReadStreamAsync(GetStreamId(id), cancellationToken))
        {
            history.Add(domainEvent switch
            {
                PublishedActRegistered registered => registered,
                ActArtifactAttached attached => attached,
                _ => throw new InvalidOperationException($"Unsupported published act domain event type '{domainEvent.GetType().Name}'.")
            });
        }

        return history.Count == 0 ? null : PublishedAct.Rehydrate(history);
    }

    public Task SaveAsync(PublishedAct act, CancellationToken cancellationToken)
    {
        return SaveAsync(act, Array.Empty<IIntegrationEvent>(), cancellationToken);
    }

    public async Task SaveAsync(
        PublishedAct act,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(act);
        ArgumentNullException.ThrowIfNull(integrationEvents);

        var pendingEvents = act.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var expectedVersion = act.Version - pendingEvents.Length;
        if (_eventStore is IEventStoreWithOutbox outboxEventStore)
        {
            await outboxEventStore.AppendAsync(
                GetStreamId(act.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                integrationEvents,
                cancellationToken);
        }
        else
        {
            await _eventStore.AppendAsync(
                GetStreamId(act.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                cancellationToken);
        }

        act.DequeueUncommittedEvents();
    }

    private static string GetStreamId(ActId id) => $"published-act:{id.Value:D}";

    private const string StreamType = "legal-corpus.published-act";

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

public sealed class SqlServerPublishedActProjectionStore(
    string connectionString,
    string schema = "lawwatcher") : IPublishedActReadRepository, IPublishedActProjection
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<IReadOnlyCollection<PublishedActReadModel>> GetActsAsync(CancellationToken cancellationToken)
    {
        var acts = new List<PublishedActReadModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [act_id],
                [bill_id],
                [bill_title],
                [bill_external_id],
                [eli],
                [title],
                [published_on],
                [effective_from],
                [artifact_kinds_json]
            FROM [{_schema}].[published_acts]
            ORDER BY [published_on] DESC, [title] ASC;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            acts.Add(new PublishedActReadModel(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                DateOnly.FromDateTime(reader.GetDateTime(6)),
                reader.IsDBNull(7) ? null : DateOnly.FromDateTime(reader.GetDateTime(7)),
                DeserializeArtifactKinds(reader.GetString(8))));
        }

        return acts;
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

        var acts = await LoadActsAsync(connection, transaction, cancellationToken);

        foreach (var domainEvent in domainEvents)
        {
            switch (domainEvent)
            {
                case PublishedActRegistered registered:
                    acts[registered.ActId.Value] = new PublishedActProjectionRecord(
                        registered.ActId.Value,
                        registered.BillId,
                        registered.BillTitle,
                        registered.BillExternalId,
                        registered.Eli,
                        registered.Title,
                        registered.PublishedOn,
                        registered.EffectiveFrom,
                        [],
                        registered.OccurredAtUtc);
                    break;
                case ActArtifactAttached attached when acts.TryGetValue(attached.ActId.Value, out var existing):
                    acts[attached.ActId.Value] = existing with
                    {
                        ArtifactKinds = existing.ArtifactKinds
                            .Append(attached.Kind)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        UpdatedAtUtc = attached.OccurredAtUtc
                    };
                    break;
            }
        }

        foreach (var act in acts.Values)
        {
            await UpsertActAsync(connection, transaction, act, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, PublishedActProjectionRecord>> LoadActsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var acts = new Dictionary<Guid, PublishedActProjectionRecord>();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT
                [act_id],
                [bill_id],
                [bill_title],
                [bill_external_id],
                [eli],
                [title],
                [published_on],
                [effective_from],
                [artifact_kinds_json],
                [updated_at_utc]
            FROM [{_schema}].[published_acts];
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var record = new PublishedActProjectionRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                DateOnly.FromDateTime(reader.GetDateTime(6)),
                reader.IsDBNull(7) ? null : DateOnly.FromDateTime(reader.GetDateTime(7)),
                DeserializeArtifactKinds(reader.GetString(8)).ToArray(),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc)));
            acts[record.Id] = record;
        }

        return acts;
    }

    private async Task UpsertActAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PublishedActProjectionRecord act,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[published_acts]
            SET
                [bill_id] = @billId,
                [bill_title] = @billTitle,
                [bill_external_id] = @billExternalId,
                [eli] = @eli,
                [title] = @title,
                [published_on] = @publishedOn,
                [effective_from] = @effectiveFrom,
                [artifact_kinds_json] = @artifactKindsJson,
                [updated_at_utc] = @updatedAtUtc
            WHERE [act_id] = @actId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[published_acts]
                (
                    [act_id],
                    [bill_id],
                    [bill_title],
                    [bill_external_id],
                    [eli],
                    [title],
                    [published_on],
                    [effective_from],
                    [artifact_kinds_json],
                    [updated_at_utc]
                )
                VALUES
                (
                    @actId,
                    @billId,
                    @billTitle,
                    @billExternalId,
                    @eli,
                    @title,
                    @publishedOn,
                    @effectiveFrom,
                    @artifactKindsJson,
                    @updatedAtUtc
                );
            END
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@actId", act.Id);
        command.Parameters.AddWithValue("@billId", act.BillId);
        command.Parameters.AddWithValue("@billTitle", act.BillTitle);
        command.Parameters.AddWithValue("@billExternalId", act.BillExternalId);
        command.Parameters.AddWithValue("@eli", act.Eli);
        command.Parameters.AddWithValue("@title", act.Title);
        command.Parameters.AddWithValue("@publishedOn", act.PublishedOn.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@effectiveFrom", act.EffectiveFrom?.ToDateTime(TimeOnly.MinValue) is DateTime effectiveFrom
            ? effectiveFrom
            : DBNull.Value);
        command.Parameters.AddWithValue("@artifactKindsJson", JsonSerializer.Serialize(act.ArtifactKinds));
        command.Parameters.AddWithValue("@updatedAtUtc", act.UpdatedAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyCollection<string> DeserializeArtifactKinds(string artifactKindsJson)
    {
        return JsonSerializer.Deserialize<string[]>(artifactKindsJson) ?? [];
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

    private sealed record PublishedActProjectionRecord(
        Guid Id,
        Guid BillId,
        string BillTitle,
        string BillExternalId,
        string Eli,
        string Title,
        DateOnly PublishedOn,
        DateOnly? EffectiveFrom,
        string[] ArtifactKinds,
        DateTimeOffset UpdatedAtUtc);
}
