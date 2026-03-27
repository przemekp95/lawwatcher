using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.TaxonomyAndProfiles.Application;
using LawWatcher.TaxonomyAndProfiles.Domain.Subscriptions;
using Microsoft.Data.SqlClient;

namespace LawWatcher.TaxonomyAndProfiles.Infrastructure;

public sealed class SqlServerProfileSubscriptionRepository(
    IEventStore eventStore,
    string connectionString,
    string schema = "lawwatcher") : IProfileSubscriptionRepository, IProfileSubscriptionOutboxWriter
{
    private readonly IEventStore _eventStore = eventStore;
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<ProfileSubscription?> GetAsync(ProfileSubscriptionId id, CancellationToken cancellationToken)
    {
        var history = new List<IDomainEvent>();
        await foreach (var domainEvent in _eventStore.ReadStreamAsync(GetStreamId(id), cancellationToken))
        {
            history.Add(domainEvent switch
            {
                ProfileSubscriptionCreated created => created,
                ProfileSubscriptionAlertPolicyChanged changed => changed,
                ProfileSubscriptionDeactivated deactivated => deactivated,
                _ => throw new InvalidOperationException($"Unsupported profile subscription domain event type '{domainEvent.GetType().Name}'.")
            });
        }

        return history.Count == 0 ? null : ProfileSubscription.Rehydrate(history);
    }

    public Task SaveAsync(ProfileSubscription subscription, CancellationToken cancellationToken)
    {
        return SaveAsync(subscription, Array.Empty<IIntegrationEvent>(), cancellationToken);
    }

    public async Task SaveAsync(
        ProfileSubscription subscription,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var pendingEvents = subscription.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var expectedVersion = subscription.Version - pendingEvents.Length;
        if (_eventStore is IEventStoreWithOutbox outboxEventStore)
        {
            await outboxEventStore.AppendAsync(
                GetStreamId(subscription.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                integrationEvents,
                cancellationToken);
        }
        else
        {
            await _eventStore.AppendAsync(
                GetStreamId(subscription.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                cancellationToken);
        }

        subscription.DequeueUncommittedEvents();
    }

    private static string GetStreamId(ProfileSubscriptionId id) => $"profile-subscription:{id.Value:D}";

    private const string StreamType = "taxonomy-and-profiles.profile-subscription";

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

public sealed class SqlServerProfileSubscriptionProjectionStore(
    string connectionString,
    string schema = "lawwatcher") : IProfileSubscriptionReadRepository, IProfileSubscriptionProjection
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task<IReadOnlyCollection<ProfileSubscriptionReadModel>> GetSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var subscriptions = new List<ProfileSubscriptionReadModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"""
            SELECT
                [subscription_id],
                [profile_id],
                [profile_name],
                [subscriber],
                [channel],
                [alert_policy],
                [digest_interval_minutes]
            FROM [{_schema}].[profile_subscriptions];
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            subscriptions.Add(new ProfileSubscriptionReadModel(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : TimeSpan.FromMinutes(reader.GetInt32(6))));
        }

        return subscriptions;
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
                case ProfileSubscriptionCreated created:
                    await UpsertCreatedAsync(connection, transaction, created, cancellationToken);
                    break;
                case ProfileSubscriptionAlertPolicyChanged changed:
                    await UpdateAlertPolicyAsync(connection, transaction, changed, cancellationToken);
                    break;
                case ProfileSubscriptionDeactivated deactivated:
                    await DeleteAsync(connection, transaction, deactivated, cancellationToken);
                    break;
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task UpsertCreatedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ProfileSubscriptionCreated created,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[profile_subscriptions]
            SET
                [profile_id] = @profileId,
                [profile_name] = @profileName,
                [subscriber] = @subscriber,
                [channel] = @channel,
                [alert_policy] = @alertPolicy,
                [digest_interval_minutes] = @digestIntervalMinutes,
                [updated_at_utc] = @updatedAtUtc
            WHERE [subscription_id] = @subscriptionId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO [{_schema}].[profile_subscriptions]
                (
                    [subscription_id],
                    [profile_id],
                    [profile_name],
                    [subscriber],
                    [channel],
                    [alert_policy],
                    [digest_interval_minutes],
                    [updated_at_utc]
                )
                VALUES
                (
                    @subscriptionId,
                    @profileId,
                    @profileName,
                    @subscriber,
                    @channel,
                    @alertPolicy,
                    @digestIntervalMinutes,
                    @updatedAtUtc
                );
            END
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@subscriptionId", created.SubscriptionId.Value);
        command.Parameters.AddWithValue("@profileId", created.ProfileId);
        command.Parameters.AddWithValue("@profileName", created.ProfileName);
        command.Parameters.AddWithValue("@subscriber", created.Subscriber);
        command.Parameters.AddWithValue("@channel", created.ChannelCode);
        command.Parameters.AddWithValue("@alertPolicy", created.AlertPolicyCode);
        command.Parameters.AddWithValue("@digestIntervalMinutes", created.DigestInterval.HasValue
            ? Convert.ToInt32(created.DigestInterval.Value.TotalMinutes)
            : DBNull.Value);
        command.Parameters.AddWithValue("@updatedAtUtc", created.OccurredAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateAlertPolicyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ProfileSubscriptionAlertPolicyChanged changed,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE [{_schema}].[profile_subscriptions]
            SET
                [alert_policy] = @alertPolicy,
                [digest_interval_minutes] = @digestIntervalMinutes,
                [updated_at_utc] = @updatedAtUtc
            WHERE [subscription_id] = @subscriptionId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@subscriptionId", changed.SubscriptionId.Value);
        command.Parameters.AddWithValue("@alertPolicy", changed.AlertPolicyCode);
        command.Parameters.AddWithValue("@digestIntervalMinutes", changed.DigestInterval.HasValue
            ? Convert.ToInt32(changed.DigestInterval.Value.TotalMinutes)
            : DBNull.Value);
        command.Parameters.AddWithValue("@updatedAtUtc", changed.OccurredAtUtc.UtcDateTime);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException($"Profile subscription projection cannot apply policy change for missing subscription '{changed.SubscriptionId.Value:D}'.");
        }
    }

    private async Task DeleteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ProfileSubscriptionDeactivated deactivated,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            DELETE FROM [{_schema}].[profile_subscriptions]
            WHERE [subscription_id] = @subscriptionId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@subscriptionId", deactivated.SubscriptionId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
