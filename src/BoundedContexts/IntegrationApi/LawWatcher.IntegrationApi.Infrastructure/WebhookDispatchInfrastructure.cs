using LawWatcher.IntegrationApi.Application;
using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.Notifications.Application;
using Microsoft.Data.SqlClient;

namespace LawWatcher.IntegrationApi.Infrastructure;

public sealed class BillAlertWebhookReadRepositoryAdapter(IBillAlertReadRepository inner)
    : IWebhookAlertReadRepository
{
    public async Task<IReadOnlyCollection<WebhookAlertReadModel>> GetAlertsAsync(CancellationToken cancellationToken)
    {
        var alerts = await inner.GetAlertsAsync(cancellationToken);

        return alerts
            .Select(alert => new WebhookAlertReadModel(
                alert.Id,
                alert.ProfileId,
                alert.ProfileName,
                alert.BillId,
                alert.BillTitle,
                alert.BillExternalId,
                alert.BillSubmittedOn,
                alert.AlertPolicy,
                alert.MatchedKeywords,
                alert.CreatedAtUtc))
            .ToArray();
    }
}

public sealed class InMemoryWebhookEventDispatchStore : IWebhookEventDispatchStore
{
    private readonly Dictionary<string, WebhookEventDispatchReadModel> _dispatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public Task<bool> HasDispatchedAsync(Guid alertId, Guid registrationId, string eventType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_dispatches.ContainsKey(GetKey(alertId, registrationId, eventType)));
        }
    }

    public Task SaveAsync(WebhookEventDispatchReadModel dispatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _dispatches[GetKey(dispatch.AlertId, dispatch.RegistrationId, dispatch.EventType)] = dispatch;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<WebhookEventDispatchReadModel>> GetDispatchesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyCollection<WebhookEventDispatchReadModel>>(_dispatches.Values.ToArray());
        }
    }

    private static string GetKey(Guid alertId, Guid registrationId, string eventType) =>
        $"{alertId:D}:{registrationId:D}:{eventType.Trim()}";
}

public sealed class FileBackedWebhookEventDispatchStore(string rootPath) : IWebhookEventDispatchStore
{
    private readonly string _rootPath = rootPath;
    private readonly string _dispatchesPath = Path.Combine(rootPath, "dispatches.json");

    public Task<bool> HasDispatchedAsync(Guid alertId, Guid registrationId, string eventType, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _dispatchesPath,
                    () => new WebhookEventDispatchDocument([]),
                    ct);

                return document.Dispatches.Any(dispatch =>
                    dispatch.AlertId == alertId &&
                    dispatch.RegistrationId == registrationId &&
                    dispatch.EventType.Equals(eventType.Trim(), StringComparison.OrdinalIgnoreCase));
            },
            cancellationToken);
    }

    public Task SaveAsync(WebhookEventDispatchReadModel dispatch, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _dispatchesPath,
                    () => new WebhookEventDispatchDocument([]),
                    ct);

                var dispatches = document.Dispatches
                    .Where(existing => !(existing.AlertId == dispatch.AlertId &&
                                         existing.RegistrationId == dispatch.RegistrationId &&
                                         existing.EventType.Equals(dispatch.EventType, StringComparison.OrdinalIgnoreCase)))
                    .Append(dispatch)
                    .ToArray();

                await JsonFilePersistence.SaveAsync(
                    _dispatchesPath,
                    new WebhookEventDispatchDocument(dispatches),
                    ct);
            },
            cancellationToken);
    }

    public Task<IReadOnlyCollection<WebhookEventDispatchReadModel>> GetDispatchesAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _dispatchesPath,
                    () => new WebhookEventDispatchDocument([]),
                    ct);

                return (IReadOnlyCollection<WebhookEventDispatchReadModel>)document.Dispatches.ToArray();
            },
            cancellationToken);
    }

    private sealed record WebhookEventDispatchDocument(WebhookEventDispatchReadModel[] Dispatches);
}

public sealed class SqlServerWebhookEventDispatchStore(
    string connectionString,
    string schema = "lawwatcher") : IWebhookEventDispatchStore
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<bool> HasDispatchedAsync(Guid alertId, Guid registrationId, string eventType, CancellationToken cancellationToken)
    {
        var normalizedEventType = NormalizeEventType(eventType);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT CASE WHEN EXISTS
            (
                SELECT 1
                FROM [{_schema}].[webhook_event_dispatches]
                WHERE [alert_id] = @alertId
                  AND [registration_id] = @registrationId
                  AND [event_type] = @eventType
            )
            THEN 1 ELSE 0 END;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@alertId", alertId);
        command.Parameters.AddWithValue("@registrationId", registrationId);
        command.Parameters.AddWithValue("@eventType", normalizedEventType);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar) == 1;
    }

    public async Task SaveAsync(WebhookEventDispatchReadModel dispatch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        var normalizedEventType = NormalizeEventType(dispatch.EventType);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            UPDATE [{_schema}].[webhook_event_dispatches]
            SET
                [callback_url] = @callbackUrl,
                [dispatched_at_utc] = @dispatchedAtUtc
            WHERE [alert_id] = @alertId
              AND [registration_id] = @registrationId
              AND [event_type] = @eventType;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[webhook_event_dispatches]
                (
                    [alert_id],
                    [registration_id],
                    [event_type],
                    [callback_url],
                    [dispatched_at_utc]
                )
                VALUES
                (
                    @alertId,
                    @registrationId,
                    @eventType,
                    @callbackUrl,
                    @dispatchedAtUtc
                );
            END
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@alertId", dispatch.AlertId);
        command.Parameters.AddWithValue("@registrationId", dispatch.RegistrationId);
        command.Parameters.AddWithValue("@eventType", normalizedEventType);
        command.Parameters.AddWithValue("@callbackUrl", dispatch.CallbackUrl);
        command.Parameters.AddWithValue("@dispatchedAtUtc", dispatch.DispatchedAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<WebhookEventDispatchReadModel>> GetDispatchesAsync(CancellationToken cancellationToken)
    {
        var dispatches = new List<WebhookEventDispatchReadModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [alert_id],
                [registration_id],
                [event_type],
                [callback_url],
                [dispatched_at_utc]
            FROM [{_schema}].[webhook_event_dispatches];
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            dispatches.Add(new WebhookEventDispatchReadModel(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc))));
        }

        return dispatches;
    }

    private static string NormalizeEventType(string eventType)
    {
        var normalized = eventType.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Event type cannot be empty.", nameof(eventType));
        }

        return normalized;
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
