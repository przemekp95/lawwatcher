using System.Text.Json;
using LawWatcher.BuildingBlocks.Persistence;
using LawWatcher.BuildingBlocks.Ports;
using LawWatcher.Notifications.Application;
using LawWatcher.TaxonomyAndProfiles.Application;
using Microsoft.Data.SqlClient;

namespace LawWatcher.Notifications.Infrastructure;

public sealed class ProfileSubscriptionNotificationReadRepositoryAdapter(IProfileSubscriptionReadRepository inner)
    : INotificationSubscriptionReadRepository
{
    public async Task<IReadOnlyCollection<NotificationSubscriptionReadModel>> GetSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var subscriptions = await inner.GetSubscriptionsAsync(cancellationToken);

        return subscriptions
            .Select(subscription => new NotificationSubscriptionReadModel(
                subscription.Id,
                subscription.ProfileId,
                subscription.ProfileName,
                subscription.Subscriber,
                subscription.Channel,
                subscription.AlertPolicy,
                subscription.DigestInterval))
            .ToArray();
    }
}

public sealed record EmailNotificationDispatchRecord(
    string Recipient,
    string Subject,
    string Content,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset DispatchedAtUtc);

public sealed class InMemoryEmailNotificationChannel : INotificationChannel
{
    private readonly List<EmailNotificationDispatchRecord> _dispatches = [];
    private readonly Lock _gate = new();

    public string ChannelCode => "email";

    public Task DispatchAsync(NotificationDispatchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var record = new EmailNotificationDispatchRecord(
            request.Recipient,
            request.Subject,
            request.Content,
            new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow);

        lock (_gate)
        {
            _dispatches.Add(record);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EmailNotificationDispatchRecord>> GetDispatchesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<EmailNotificationDispatchRecord>>(_dispatches.ToArray());
        }
    }
}

public sealed class WebhookNotificationChannel(IWebhookDispatcher dispatcher) : INotificationChannel
{
    public string ChannelCode => "webhook";

    public Task DispatchAsync(NotificationDispatchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payload = JsonSerializer.Serialize(new
        {
            subject = request.Subject,
            content = request.Content,
            metadata = request.Metadata
        });

        return dispatcher.DispatchAsync(new WebhookDispatchRequest(
            request.Recipient,
            request.EventType,
            payload,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-LawWatcher-Signature"] = "sha256=local-dev",
                ["X-LawWatcher-Channel"] = ChannelCode
            }), cancellationToken);
    }
}

public sealed class InMemoryAlertNotificationDispatchStore : IAlertNotificationDispatchStore
{
    private readonly Dictionary<string, AlertNotificationDispatchReadModel> _dispatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public Task<bool> HasDispatchedAsync(Guid alertId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_dispatches.ContainsKey(GetKey(alertId, subscriptionId)));
        }
    }

    public Task SaveAsync(AlertNotificationDispatchReadModel dispatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _dispatches[GetKey(dispatch.AlertId, dispatch.SubscriptionId)] = dispatch;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<AlertNotificationDispatchReadModel>> GetDispatchesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyCollection<AlertNotificationDispatchReadModel>>(_dispatches.Values.ToArray());
        }
    }

    private static string GetKey(Guid alertId, Guid subscriptionId) => $"{alertId:D}:{subscriptionId:D}";
}

public sealed class FileBackedAlertNotificationDispatchStore(string rootPath) : IAlertNotificationDispatchStore
{
    private readonly string _rootPath = rootPath;
    private readonly string _dispatchesPath = Path.Combine(rootPath, "dispatches.json");

    public Task<bool> HasDispatchedAsync(Guid alertId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _dispatchesPath,
                    () => new AlertNotificationDispatchDocument([]),
                    ct);

                return document.Dispatches.Any(dispatch =>
                    dispatch.AlertId == alertId &&
                    dispatch.SubscriptionId == subscriptionId);
            },
            cancellationToken);
    }

    public Task SaveAsync(AlertNotificationDispatchReadModel dispatch, CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _dispatchesPath,
                    () => new AlertNotificationDispatchDocument([]),
                    ct);

                var dispatches = document.Dispatches
                    .Where(existing => !(existing.AlertId == dispatch.AlertId && existing.SubscriptionId == dispatch.SubscriptionId))
                    .Append(dispatch)
                    .ToArray();

                await JsonFilePersistence.SaveAsync(
                    _dispatchesPath,
                    new AlertNotificationDispatchDocument(dispatches),
                    ct);
            },
            cancellationToken);
    }

    public Task<IReadOnlyCollection<AlertNotificationDispatchReadModel>> GetDispatchesAsync(CancellationToken cancellationToken)
    {
        return JsonFilePersistence.ExecuteLockedAsync(
            _rootPath,
            async ct =>
            {
                var document = await JsonFilePersistence.LoadAsync(
                    _dispatchesPath,
                    () => new AlertNotificationDispatchDocument([]),
                    ct);

                return (IReadOnlyCollection<AlertNotificationDispatchReadModel>)document.Dispatches.ToArray();
            },
            cancellationToken);
    }

    private sealed record AlertNotificationDispatchDocument(AlertNotificationDispatchReadModel[] Dispatches);
}

public sealed class SqlServerAlertNotificationDispatchStore(
    string connectionString,
    string schema = "lawwatcher") : IAlertNotificationDispatchStore
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<bool> HasDispatchedAsync(Guid alertId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT CASE WHEN EXISTS
            (
                SELECT 1
                FROM [{_schema}].[alert_notification_dispatches]
                WHERE [alert_id] = @alertId
                  AND [subscription_id] = @subscriptionId
            )
            THEN 1 ELSE 0 END;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@alertId", alertId);
        command.Parameters.AddWithValue("@subscriptionId", subscriptionId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar) == 1;
    }

    public async Task SaveAsync(AlertNotificationDispatchReadModel dispatch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            UPDATE [{_schema}].[alert_notification_dispatches]
            SET
                [profile_name] = @profileName,
                [bill_title] = @billTitle,
                [channel] = @channel,
                [recipient] = @recipient,
                [dispatched_at_utc] = @dispatchedAtUtc
            WHERE [alert_id] = @alertId
              AND [subscription_id] = @subscriptionId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[alert_notification_dispatches]
                (
                    [alert_id],
                    [subscription_id],
                    [profile_name],
                    [bill_title],
                    [channel],
                    [recipient],
                    [dispatched_at_utc]
                )
                VALUES
                (
                    @alertId,
                    @subscriptionId,
                    @profileName,
                    @billTitle,
                    @channel,
                    @recipient,
                    @dispatchedAtUtc
                );
            END
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@alertId", dispatch.AlertId);
        command.Parameters.AddWithValue("@subscriptionId", dispatch.SubscriptionId);
        command.Parameters.AddWithValue("@profileName", dispatch.ProfileName);
        command.Parameters.AddWithValue("@billTitle", dispatch.BillTitle);
        command.Parameters.AddWithValue("@channel", dispatch.Channel);
        command.Parameters.AddWithValue("@recipient", dispatch.Recipient);
        command.Parameters.AddWithValue("@dispatchedAtUtc", dispatch.DispatchedAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<AlertNotificationDispatchReadModel>> GetDispatchesAsync(CancellationToken cancellationToken)
    {
        var dispatches = new List<AlertNotificationDispatchReadModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [alert_id],
                [subscription_id],
                [profile_name],
                [bill_title],
                [channel],
                [recipient],
                [dispatched_at_utc]
            FROM [{_schema}].[alert_notification_dispatches];
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            dispatches.Add(new AlertNotificationDispatchReadModel(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc))));
        }

        return dispatches;
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
