using System.Text.Json;
using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.IdentityAndAccess.Application;
using LawWatcher.IdentityAndAccess.Domain.OperatorAccounts;
using Microsoft.Data.SqlClient;

namespace LawWatcher.IdentityAndAccess.Infrastructure;

public sealed class SqlServerOperatorAccountRepository(
    IEventStore eventStore,
    string connectionString,
    string schema = "lawwatcher") : IOperatorAccountRepository
{
    private readonly IEventStore _eventStore = eventStore;
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<OperatorAccount?> GetAsync(OperatorAccountId id, CancellationToken cancellationToken)
    {
        var history = new List<IDomainEvent>();
        await foreach (var domainEvent in _eventStore.ReadStreamAsync(GetStreamId(id), cancellationToken))
        {
            history.Add(domainEvent switch
            {
                OperatorAccountRegistered registered => registered,
                OperatorAccountUpdated updated => updated,
                OperatorPasswordReset reset => reset,
                OperatorAccountDeactivated deactivated => deactivated,
                _ => throw new InvalidOperationException($"Unsupported operator account domain event type '{domainEvent.GetType().Name}'.")
            });
        }

        return history.Count == 0 ? null : OperatorAccount.Rehydrate(history);
    }

    public async Task SaveAsync(OperatorAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var pendingEvents = account.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var expectedVersion = account.Version - pendingEvents.Length;
        await _eventStore.AppendAsync(
            GetStreamId(account.Id),
            StreamType,
            expectedVersion,
            pendingEvents,
            cancellationToken);

        account.DequeueUncommittedEvents();
    }

    private static string GetStreamId(OperatorAccountId id) => $"operator-account:{id.Value:D}";

    private const string StreamType = "identity-and-access.operator-account";

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

public sealed class SqlServerOperatorAccountProjectionStore(
    string connectionString,
    string schema = "lawwatcher") : IOperatorAccountReadRepository, IOperatorAccountProjection
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<IReadOnlyCollection<OperatorAccountReadModel>> GetOperatorsAsync(CancellationToken cancellationToken)
    {
        var operators = new List<OperatorAccountReadModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [operator_id],
                [email],
                [display_name],
                [password_hash],
                [permissions_json],
                [is_active],
                [registered_at_utc]
            FROM [{_schema}].[operator_accounts]
            ORDER BY [email] ASC;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            operators.Add(new OperatorAccountReadModel(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DeserializePermissions(reader.GetString(4)),
                reader.GetBoolean(5),
                new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero)));
        }

        return operators;
    }

    public async Task<OperatorAccountReadModel?> GetByIdAsync(Guid operatorId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [operator_id],
                [email],
                [display_name],
                [password_hash],
                [permissions_json],
                [is_active],
                [registered_at_utc]
            FROM [{_schema}].[operator_accounts]
            WHERE [operator_id] = @operatorId;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@operatorId", operatorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OperatorAccountReadModel(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            DeserializePermissions(reader.GetString(4)),
            reader.GetBoolean(5),
            new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero));
    }

    public async Task<OperatorAccountReadModel?> GetByEmailAsync(string email, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [operator_id],
                [email],
                [display_name],
                [password_hash],
                [permissions_json],
                [is_active],
                [registered_at_utc]
            FROM [{_schema}].[operator_accounts]
            WHERE [email] = @email;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@email", email.Trim().ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OperatorAccountReadModel(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            DeserializePermissions(reader.GetString(4)),
            reader.GetBoolean(5),
            new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero));
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
                case OperatorAccountRegistered registered:
                    await UpsertRegisteredAsync(connection, transaction, registered, cancellationToken);
                    break;
                case OperatorAccountUpdated updated:
                    await UpdateProfileAsync(connection, transaction, updated, cancellationToken);
                    break;
                case OperatorPasswordReset reset:
                    await UpdatePasswordAsync(connection, transaction, reset, cancellationToken);
                    break;
                case OperatorAccountDeactivated deactivated:
                    await UpdateDeactivatedAsync(connection, transaction, deactivated, cancellationToken);
                    break;
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task UpsertRegisteredAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperatorAccountRegistered registered,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[operator_accounts]
            SET
                [email] = @email,
                [display_name] = @displayName,
                [password_hash] = @passwordHash,
                [permissions_json] = @permissionsJson,
                [is_active] = @isActive,
                [registered_at_utc] = @registeredAtUtc,
                [updated_at_utc] = @updatedAtUtc
            WHERE [operator_id] = @operatorId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[operator_accounts]
                (
                    [operator_id],
                    [email],
                    [display_name],
                    [password_hash],
                    [permissions_json],
                    [is_active],
                    [registered_at_utc],
                    [updated_at_utc]
                )
                VALUES
                (
                    @operatorId,
                    @email,
                    @displayName,
                    @passwordHash,
                    @permissionsJson,
                    @isActive,
                    @registeredAtUtc,
                    @updatedAtUtc
                );
            END
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@operatorId", registered.OperatorId.Value);
        command.Parameters.AddWithValue("@email", registered.Email);
        command.Parameters.AddWithValue("@displayName", registered.DisplayName);
        command.Parameters.AddWithValue("@passwordHash", registered.PasswordHash);
        command.Parameters.AddWithValue("@permissionsJson", JsonSerializer.Serialize(
            registered.Permissions.OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase).ToArray()));
        command.Parameters.AddWithValue("@isActive", true);
        command.Parameters.AddWithValue("@registeredAtUtc", registered.OccurredAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@updatedAtUtc", registered.OccurredAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateProfileAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperatorAccountUpdated updated,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[operator_accounts]
            SET
                [display_name] = @displayName,
                [permissions_json] = @permissionsJson,
                [updated_at_utc] = @updatedAtUtc
            WHERE [operator_id] = @operatorId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@operatorId", updated.OperatorId.Value);
        command.Parameters.AddWithValue("@displayName", updated.DisplayName);
        command.Parameters.AddWithValue("@permissionsJson", JsonSerializer.Serialize(
            updated.Permissions.OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase).ToArray()));
        command.Parameters.AddWithValue("@updatedAtUtc", updated.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"Operator account projection cannot apply update event for missing operator '{updated.OperatorId.Value:D}'.");
        }
    }

    private async Task UpdatePasswordAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperatorPasswordReset reset,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[operator_accounts]
            SET
                [password_hash] = @passwordHash,
                [updated_at_utc] = @updatedAtUtc
            WHERE [operator_id] = @operatorId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@operatorId", reset.OperatorId.Value);
        command.Parameters.AddWithValue("@passwordHash", reset.PasswordHash);
        command.Parameters.AddWithValue("@updatedAtUtc", reset.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"Operator account projection cannot apply password reset event for missing operator '{reset.OperatorId.Value:D}'.");
        }
    }

    private async Task UpdateDeactivatedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        OperatorAccountDeactivated deactivated,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[operator_accounts]
            SET
                [is_active] = @isActive,
                [updated_at_utc] = @updatedAtUtc
            WHERE [operator_id] = @operatorId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@operatorId", deactivated.OperatorId.Value);
        command.Parameters.AddWithValue("@isActive", false);
        command.Parameters.AddWithValue("@updatedAtUtc", deactivated.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"Operator account projection cannot apply deactivation event for missing operator '{deactivated.OperatorId.Value:D}'.");
        }
    }

    private static IReadOnlyCollection<string> DeserializePermissions(string permissionsJson)
    {
        return JsonSerializer.Deserialize<string[]>(permissionsJson) ?? [];
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
