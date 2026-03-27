using System.Text.Json;
using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Domain.Webhooks;
using Microsoft.Data.SqlClient;

namespace LawWatcher.IntegrationApi.Infrastructure;

public sealed class SqlServerWebhookRegistrationRepository(
    IEventStore eventStore,
    string connectionString,
    string schema = "lawwatcher") : IWebhookRegistrationRepository, IWebhookRegistrationOutboxWriter
{
    private readonly IEventStore _eventStore = eventStore;
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<WebhookRegistration?> GetAsync(WebhookRegistrationId id, CancellationToken cancellationToken)
    {
        var history = new List<IDomainEvent>();
        await foreach (var domainEvent in _eventStore.ReadStreamAsync(GetStreamId(id), cancellationToken))
        {
            history.Add(domainEvent);
        }

        return history.Count == 0 ? null : WebhookRegistration.Rehydrate(history);
    }

    public Task SaveAsync(WebhookRegistration registration, CancellationToken cancellationToken)
    {
        return SaveAsync(registration, Array.Empty<IIntegrationEvent>(), cancellationToken);
    }

    public async Task SaveAsync(
        WebhookRegistration registration,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var pendingEvents = registration.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var expectedVersion = registration.Version - pendingEvents.Length;
        if (_eventStore is IEventStoreWithOutbox outboxEventStore)
        {
            await outboxEventStore.AppendAsync(
                GetStreamId(registration.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                integrationEvents,
                cancellationToken);
        }
        else
        {
            await _eventStore.AppendAsync(
                GetStreamId(registration.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                cancellationToken);
        }

        registration.DequeueUncommittedEvents();
    }

    private static string GetStreamId(WebhookRegistrationId id) => $"webhook-registration:{id.Value:D}";

    private const string StreamType = "integration-api.webhook-registration";

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

public sealed class SqlServerWebhookRegistrationProjectionStore(
    string connectionString,
    string schema = "lawwatcher") : IWebhookRegistrationReadRepository, IWebhookRegistrationProjection
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<IReadOnlyCollection<WebhookRegistrationReadModel>> GetWebhooksAsync(CancellationToken cancellationToken)
    {
        var webhooks = new List<WebhookRegistrationReadModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [registration_id],
                [name],
                [callback_url],
                [event_types_json],
                [is_active]
            FROM [{_schema}].[webhook_registrations];
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            webhooks.Add(new WebhookRegistrationReadModel(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                DeserializeEventTypes(reader.GetString(3)),
                reader.GetBoolean(4)));
        }

        return webhooks;
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
                case WebhookRegistered registered:
                    await UpsertRegisteredAsync(connection, transaction, registered, cancellationToken);
                    break;
                case WebhookUpdated updated:
                    await UpsertUpdatedAsync(connection, transaction, updated, cancellationToken);
                    break;
                case WebhookDeactivated deactivated:
                    await UpdateDeactivatedAsync(connection, transaction, deactivated, cancellationToken);
                    break;
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task UpsertRegisteredAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WebhookRegistered registered,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[webhook_registrations]
            SET
                [name] = @name,
                [callback_url] = @callbackUrl,
                [event_types_json] = @eventTypesJson,
                [is_active] = @isActive,
                [updated_at_utc] = @updatedAtUtc
            WHERE [registration_id] = @registrationId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[webhook_registrations]
                (
                    [registration_id],
                    [name],
                    [callback_url],
                    [event_types_json],
                    [is_active],
                    [updated_at_utc]
                )
                VALUES
                (
                    @registrationId,
                    @name,
                    @callbackUrl,
                    @eventTypesJson,
                    @isActive,
                    @updatedAtUtc
                );
            END
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@registrationId", registered.RegistrationId.Value);
        command.Parameters.AddWithValue("@name", registered.Name);
        command.Parameters.AddWithValue("@callbackUrl", registered.CallbackUrl);
        command.Parameters.AddWithValue("@eventTypesJson", JsonSerializer.Serialize(
            registered.EventTypes
                .OrderBy(eventType => eventType, StringComparer.OrdinalIgnoreCase)
                .ToArray()));
        command.Parameters.AddWithValue("@isActive", true);
        command.Parameters.AddWithValue("@updatedAtUtc", registered.OccurredAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateDeactivatedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WebhookDeactivated deactivated,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[webhook_registrations]
            SET
                [is_active] = @isActive,
                [updated_at_utc] = @updatedAtUtc
            WHERE [registration_id] = @registrationId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@registrationId", deactivated.RegistrationId.Value);
        command.Parameters.AddWithValue("@isActive", false);
        command.Parameters.AddWithValue("@updatedAtUtc", deactivated.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"Webhook projection cannot apply deactivation event for missing registration '{deactivated.RegistrationId.Value:D}'.");
        }
    }

    private async Task UpsertUpdatedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WebhookUpdated updated,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[webhook_registrations]
            SET
                [name] = @name,
                [callback_url] = @callbackUrl,
                [event_types_json] = @eventTypesJson,
                [is_active] = @isActive,
                [updated_at_utc] = @updatedAtUtc
            WHERE [registration_id] = @registrationId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@registrationId", updated.RegistrationId.Value);
        command.Parameters.AddWithValue("@name", updated.Name);
        command.Parameters.AddWithValue("@callbackUrl", updated.CallbackUrl);
        command.Parameters.AddWithValue("@eventTypesJson", JsonSerializer.Serialize(
            updated.EventTypes
                .OrderBy(eventType => eventType, StringComparer.OrdinalIgnoreCase)
                .ToArray()));
        command.Parameters.AddWithValue("@isActive", true);
        command.Parameters.AddWithValue("@updatedAtUtc", updated.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"Webhook projection cannot apply update event for missing registration '{updated.RegistrationId.Value:D}'.");
        }
    }

    private static IReadOnlyCollection<string> DeserializeEventTypes(string eventTypesJson)
    {
        return JsonSerializer.Deserialize<string[]>(eventTypesJson) ?? [];
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
