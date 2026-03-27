using System.Text.Json;
using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.TaxonomyAndProfiles.Application;
using LawWatcher.TaxonomyAndProfiles.Domain.MonitoringProfiles;
using Microsoft.Data.SqlClient;

namespace LawWatcher.TaxonomyAndProfiles.Infrastructure;

public sealed class SqlServerMonitoringProfileRepository(IEventStore eventStore) : IMonitoringProfileRepository, IMonitoringProfileOutboxWriter
{
    private const string StreamType = "taxonomy-and-profiles.monitoring-profile";

    private readonly IEventStore _eventStore = eventStore;

    public async Task<MonitoringProfile?> GetAsync(MonitoringProfileId id, CancellationToken cancellationToken)
    {
        var history = new List<IDomainEvent>();

        await foreach (var domainEvent in _eventStore.ReadStreamAsync(GetStreamId(id), cancellationToken))
        {
            history.Add(domainEvent switch
            {
                MonitoringProfileCreated created => created,
                MonitoringProfileRuleAdded added => added,
                MonitoringProfileAlertPolicyChanged changed => changed,
                MonitoringProfileDeactivated deactivated => deactivated,
                _ => throw new InvalidOperationException($"Unsupported monitoring profile domain event type '{domainEvent.GetType().Name}'.")
            });
        }

        return history.Count == 0 ? null : MonitoringProfile.Rehydrate(history);
    }

    public Task SaveAsync(MonitoringProfile profile, CancellationToken cancellationToken)
    {
        return SaveAsync(profile, Array.Empty<IIntegrationEvent>(), cancellationToken);
    }

    public async Task SaveAsync(
        MonitoringProfile profile,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        var pendingEvents = profile.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var expectedVersion = profile.Version - pendingEvents.Length;
        if (_eventStore is IEventStoreWithOutbox outboxEventStore)
        {
            await outboxEventStore.AppendAsync(
                GetStreamId(profile.Id),
                StreamType,
                expectedVersion,
                pendingEvents,
                integrationEvents,
                cancellationToken);
        }
        else
        {
            await _eventStore.AppendAsync(GetStreamId(profile.Id), StreamType, expectedVersion, pendingEvents, cancellationToken);
        }

        profile.DequeueUncommittedEvents();
    }

    private static string GetStreamId(MonitoringProfileId id) => $"monitoring-profile:{id.Value:D}";
}

public sealed class SqlServerMonitoringProfileProjectionStore(string connectionString, string schema = "lawwatcher")
    : IMonitoringProfileReadRepository, IMonitoringProfileProjection
{
    private readonly string _connectionString = connectionString;
    private readonly string _schema = schema;

    public async Task<IReadOnlyCollection<MonitoringProfileReadModel>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        var profiles = new List<MonitoringProfileReadModel>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT [profile_id], [name], [alert_policy], [keywords_json]
            FROM [{_schema}].[monitoring_profile_projections]
            ORDER BY [name] ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profiles.Add(new MonitoringProfileReadModel(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                DeserializeKeywords(reader.GetString(3))));
        }

        return profiles;
    }

    public async Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        if (domainEvents.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var dbTransaction = await connection.BeginTransactionAsync(cancellationToken);
        var transaction = (SqlTransaction)dbTransaction;

        var profiles = await LoadProfilesAsync(connection, transaction, cancellationToken);
        var touchedProfileIds = new HashSet<Guid>();

        foreach (var domainEvent in domainEvents)
        {
            switch (domainEvent)
            {
                case MonitoringProfileCreated created:
                    profiles[created.ProfileId.Value] = new MonitoringProfileProjectionRecord(
                        created.ProfileId.Value,
                        created.Name,
                        created.AlertPolicyCode,
                        [],
                        created.OccurredAtUtc);
                    touchedProfileIds.Add(created.ProfileId.Value);
                    break;
                case MonitoringProfileRuleAdded added when profiles.TryGetValue(added.ProfileId.Value, out var profile):
                    if (!string.Equals(added.RuleKind, "keyword", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    profiles[added.ProfileId.Value] = profile with
                    {
                        Keywords = profile.Keywords
                            .Append(added.RuleValue)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(keyword => keyword, StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        UpdatedAtUtc = added.OccurredAtUtc
                    };
                    touchedProfileIds.Add(added.ProfileId.Value);
                    break;
                case MonitoringProfileAlertPolicyChanged changed when profiles.TryGetValue(changed.ProfileId.Value, out var changedProfile):
                    profiles[changed.ProfileId.Value] = changedProfile with
                    {
                        AlertPolicy = changed.AlertPolicyCode,
                        UpdatedAtUtc = changed.OccurredAtUtc
                    };
                    touchedProfileIds.Add(changed.ProfileId.Value);
                    break;
                case MonitoringProfileDeactivated deactivated:
                    profiles.Remove(deactivated.ProfileId.Value);
                    touchedProfileIds.Add(deactivated.ProfileId.Value);
                    break;
            }
        }

        foreach (var profileId in touchedProfileIds)
        {
            if (profiles.TryGetValue(profileId, out var profile))
            {
                await UpsertProfileAsync(connection, transaction, profile, cancellationToken);
                continue;
            }

            await DeleteProfileAsync(connection, transaction, profileId, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, MonitoringProfileProjectionRecord>> LoadProfilesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var profiles = new Dictionary<Guid, MonitoringProfileProjectionRecord>();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT [profile_id], [name], [alert_policy], [keywords_json], [updated_at_utc]
            FROM [{_schema}].[monitoring_profile_projections];
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var record = new MonitoringProfileProjectionRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                DeserializeKeywords(reader.GetString(3)).ToArray(),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)));
            profiles[record.Id] = record;
        }

        return profiles;
    }

    private async Task UpsertProfileAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MonitoringProfileProjectionRecord profile,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            IF EXISTS (SELECT 1 FROM [{_schema}].[monitoring_profile_projections] WHERE [profile_id] = @profileId)
            BEGIN
                UPDATE [{_schema}].[monitoring_profile_projections]
                SET [name] = @name,
                    [alert_policy] = @alertPolicy,
                    [keywords_json] = @keywordsJson,
                    [updated_at_utc] = @updatedAtUtc
                WHERE [profile_id] = @profileId;
            END
            ELSE
            BEGIN
                INSERT INTO [{_schema}].[monitoring_profile_projections]
                    ([profile_id], [name], [alert_policy], [keywords_json], [updated_at_utc])
                VALUES
                    (@profileId, @name, @alertPolicy, @keywordsJson, @updatedAtUtc);
            END
            """;

        command.Parameters.AddWithValue("@profileId", profile.Id);
        command.Parameters.AddWithValue("@name", profile.Name);
        command.Parameters.AddWithValue("@alertPolicy", profile.AlertPolicy);
        command.Parameters.AddWithValue("@keywordsJson", JsonSerializer.Serialize(profile.Keywords));
        command.Parameters.AddWithValue("@updatedAtUtc", profile.UpdatedAtUtc);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteProfileAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            DELETE FROM [{_schema}].[monitoring_profile_projections]
            WHERE [profile_id] = @profileId;
            """;

        command.Parameters.AddWithValue("@profileId", profileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyCollection<string> DeserializeKeywords(string keywordsJson)
    {
        return JsonSerializer.Deserialize<string[]>(keywordsJson) ?? [];
    }

    private sealed record MonitoringProfileProjectionRecord(
        Guid Id,
        string Name,
        string AlertPolicy,
        string[] Keywords,
        DateTimeOffset UpdatedAtUtc);
}
