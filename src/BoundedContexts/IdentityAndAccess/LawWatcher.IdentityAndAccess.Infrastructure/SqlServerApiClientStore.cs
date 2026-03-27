using System.Text.Json;
using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IdentityAndAccess.Application;
using LawWatcher.IdentityAndAccess.Domain.ApiClients;
using Microsoft.Data.SqlClient;

namespace LawWatcher.IdentityAndAccess.Infrastructure;

public sealed class SqlServerApiClientRepository(
    IEventStore eventStore,
    string connectionString,
    string schema = "lawwatcher") : IApiClientRepository
{
    private readonly IEventStore _eventStore = eventStore;
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<ApiClient?> GetAsync(ApiClientId id, CancellationToken cancellationToken)
    {
        var history = new List<IDomainEvent>();
        await foreach (var domainEvent in _eventStore.ReadStreamAsync(GetStreamId(id), cancellationToken))
        {
            history.Add(domainEvent switch
            {
                ApiClientRegistered registered => registered,
                ApiClientUpdated updated => updated,
                ApiClientDeactivated deactivated => deactivated,
                _ => throw new InvalidOperationException($"Unsupported API client domain event type '{domainEvent.GetType().Name}'.")
            });
        }

        return history.Count == 0 ? null : ApiClient.Rehydrate(history);
    }

    public async Task SaveAsync(ApiClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var pendingEvents = client.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var expectedVersion = client.Version - pendingEvents.Length;
        await _eventStore.AppendAsync(
            GetStreamId(client.Id),
            StreamType,
            expectedVersion,
            pendingEvents,
            cancellationToken);

        client.DequeueUncommittedEvents();
    }

    private static string GetStreamId(ApiClientId id) => $"api-client:{id.Value:D}";

    private const string StreamType = "identity-and-access.api-client";

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

public sealed class SqlServerApiClientProjectionStore(
    string connectionString,
    string schema = "lawwatcher") : IApiClientReadRepository, IApiClientProjection
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<IReadOnlyCollection<ApiClientReadModel>> GetApiClientsAsync(CancellationToken cancellationToken)
    {
        var clients = new List<ApiClientReadModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [client_id],
                [name],
                [client_identifier],
                [token_fingerprint],
                [scopes_json],
                [is_active],
                [registered_at_utc]
            FROM [{_schema}].[api_clients]
            ORDER BY [name] ASC;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            clients.Add(new ApiClientReadModel(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DeserializeScopes(reader.GetString(4)),
                reader.GetBoolean(5),
                new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero)));
        }

        return clients;
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
                case ApiClientRegistered registered:
                    await UpsertRegisteredAsync(connection, transaction, registered, cancellationToken);
                    break;
                case ApiClientUpdated updated:
                    await UpdateClientAsync(connection, transaction, updated, cancellationToken);
                    break;
                case ApiClientDeactivated deactivated:
                    await UpdateDeactivatedAsync(connection, transaction, deactivated, cancellationToken);
                    break;
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task UpsertRegisteredAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ApiClientRegistered registered,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[api_clients]
            SET
                [name] = @name,
                [client_identifier] = @clientIdentifier,
                [token_fingerprint] = @tokenFingerprint,
                [scopes_json] = @scopesJson,
                [is_active] = @isActive,
                [registered_at_utc] = @registeredAtUtc,
                [updated_at_utc] = @updatedAtUtc
            WHERE [client_id] = @clientId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[api_clients]
                (
                    [client_id],
                    [name],
                    [client_identifier],
                    [token_fingerprint],
                    [scopes_json],
                    [is_active],
                    [registered_at_utc],
                    [updated_at_utc]
                )
                VALUES
                (
                    @clientId,
                    @name,
                    @clientIdentifier,
                    @tokenFingerprint,
                    @scopesJson,
                    @isActive,
                    @registeredAtUtc,
                    @updatedAtUtc
                );
            END
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@clientId", registered.ClientId.Value);
        command.Parameters.AddWithValue("@name", registered.Name);
        command.Parameters.AddWithValue("@clientIdentifier", registered.Identifier);
        command.Parameters.AddWithValue("@tokenFingerprint", registered.TokenFingerprint);
        command.Parameters.AddWithValue("@scopesJson", JsonSerializer.Serialize(
            registered.Scopes
                .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
                .ToArray()));
        command.Parameters.AddWithValue("@isActive", true);
        command.Parameters.AddWithValue("@registeredAtUtc", registered.OccurredAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@updatedAtUtc", registered.OccurredAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateDeactivatedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ApiClientDeactivated deactivated,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[api_clients]
            SET
                [is_active] = @isActive,
                [updated_at_utc] = @updatedAtUtc
            WHERE [client_id] = @clientId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@clientId", deactivated.ClientId.Value);
        command.Parameters.AddWithValue("@isActive", false);
        command.Parameters.AddWithValue("@updatedAtUtc", deactivated.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"API client projection cannot apply deactivation event for missing client '{deactivated.ClientId.Value:D}'.");
        }
    }

    private async Task UpdateClientAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ApiClientUpdated updated,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[api_clients]
            SET
                [name] = @name,
                [token_fingerprint] = @tokenFingerprint,
                [scopes_json] = @scopesJson,
                [is_active] = @isActive,
                [updated_at_utc] = @updatedAtUtc
            WHERE [client_id] = @clientId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@clientId", updated.ClientId.Value);
        command.Parameters.AddWithValue("@name", updated.Name);
        command.Parameters.AddWithValue("@tokenFingerprint", updated.TokenFingerprint);
        command.Parameters.AddWithValue("@scopesJson", JsonSerializer.Serialize(
            updated.Scopes
                .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
                .ToArray()));
        command.Parameters.AddWithValue("@isActive", true);
        command.Parameters.AddWithValue("@updatedAtUtc", updated.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"API client projection cannot apply update event for missing client '{updated.ClientId.Value:D}'.");
        }
    }

    private static IReadOnlyCollection<string> DeserializeScopes(string scopesJson)
    {
        return JsonSerializer.Deserialize<string[]>(scopesJson) ?? [];
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
