using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.IntegrationApi.Application;
using Microsoft.Data.SqlClient;

namespace LawWatcher.IntegrationApi.Infrastructure;

public sealed class FileBackedEventFeedProjectionStore(string rootPath) : IEventFeedReadRepository, IEventFeedProjection
{
    private readonly string _rootPath = rootPath;
    private readonly string _projectionPath = Path.Combine(rootPath, "projection.json");

    public Task<IReadOnlyCollection<EventFeedItem>> GetEventsAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _projectionPath,
                    () => new EventFeedProjectionDocument([]),
                    ct);

                return (IReadOnlyCollection<EventFeedItem>)document.Events.ToArray();
            },
            cancellationToken);
    }

    public Task ReplaceAllAsync(IReadOnlyCollection<EventFeedItem> events, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var orderedEvents = events
                    .OrderByDescending(item => item.OccurredAtUtc)
                    .ThenBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                await JsonFilePersistence.SaveAsync(
                    _projectionPath,
                    new EventFeedProjectionDocument(orderedEvents),
                    ct);
            },
            cancellationToken);
    }

    private sealed record EventFeedProjectionDocument(EventFeedItem[] Events);
}

public sealed class InMemoryEventFeedProjectionStore : IEventFeedReadRepository, IEventFeedProjection
{
    private readonly List<EventFeedItem> _events = [];

    public Task<IReadOnlyCollection<EventFeedItem>> GetEventsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult((IReadOnlyCollection<EventFeedItem>)_events.ToArray());
    }

    public Task ReplaceAllAsync(IReadOnlyCollection<EventFeedItem> events, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Clear();
        _events.AddRange(events);
        return Task.CompletedTask;
    }
}

public sealed class SqlServerEventFeedProjectionStore(
    string connectionString,
    string schema = "lawwatcher") : IEventFeedReadRepository, IEventFeedProjection
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<IReadOnlyCollection<EventFeedItem>> GetEventsAsync(CancellationToken cancellationToken)
    {
        var events = new List<EventFeedItem>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [event_id],
                [type],
                [subject_type],
                [subject_id],
                [title],
                [summary],
                [occurred_at_utc]
            FROM [{_schema}].[event_feed];
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new EventFeedItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc))));
        }

        return events;
    }

    public async Task ReplaceAllAsync(IReadOnlyCollection<EventFeedItem> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        var orderedEvents = events
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var deleteSql = $"""DELETE FROM [{_schema}].[event_feed];""";
        await using (var deleteCommand = new SqlCommand(deleteSql, connection, transaction))
        {
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var insertSql = $"""
            INSERT INTO [{_schema}].[event_feed]
            (
                [event_id],
                [type],
                [subject_type],
                [subject_id],
                [title],
                [summary],
                [occurred_at_utc]
            )
            VALUES
            (
                @eventId,
                @type,
                @subjectType,
                @subjectId,
                @title,
                @summary,
                @occurredAtUtc
            );
            """;

        foreach (var item in orderedEvents)
        {
            await using var insertCommand = new SqlCommand(insertSql, connection, transaction);
            insertCommand.Parameters.AddWithValue("@eventId", item.Id);
            insertCommand.Parameters.AddWithValue("@type", item.Type);
            insertCommand.Parameters.AddWithValue("@subjectType", item.SubjectType);
            insertCommand.Parameters.AddWithValue("@subjectId", item.SubjectId);
            insertCommand.Parameters.AddWithValue("@title", item.Title);
            insertCommand.Parameters.AddWithValue("@summary", item.Summary);
            insertCommand.Parameters.AddWithValue("@occurredAtUtc", item.OccurredAtUtc.UtcDateTime);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
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
