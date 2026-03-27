using Microsoft.Data.SqlClient;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LawWatcher.BuildingBlocks.Messaging;

public sealed class DisabledMessagingDiagnosticsStore : IMessagingDiagnosticsStore
{
    private static readonly MessagingDiagnosticsSnapshot EmptySnapshot = new(
        false,
        new OutboxDiagnosticsSnapshot(
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            null,
            Array.Empty<OutboxMessageTypeDiagnosticsSnapshot>()),
        new InboxDiagnosticsSnapshot(
            0,
            Array.Empty<InboxConsumerDiagnosticsSnapshot>()));

    public bool IsAvailable => false;

    public Task<MessagingDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EmptySnapshot);
    }
}

public sealed class SqlServerMessagingDiagnosticsStore : IMessagingDiagnosticsStore
{
    private readonly string _connectionString;
    private readonly string _schema;

    public SqlServerMessagingDiagnosticsStore(string connectionString, string schema = "lawwatcher")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _schema = ValidateSchema(schema);
    }

    public bool IsAvailable => true;

    public async Task<MessagingDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var outbox = await ReadOutboxDiagnosticsAsync(connection, cancellationToken);
        var inbox = await ReadInboxDiagnosticsAsync(connection, cancellationToken);
        return new MessagingDiagnosticsSnapshot(true, outbox, inbox);
    }

    private async Task<OutboxDiagnosticsSnapshot> ReadOutboxDiagnosticsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var summarySql = $"""
            SELECT
                COUNT(*) AS [total_count],
                ISNULL(SUM(CASE WHEN [status] = 'pending' THEN 1 ELSE 0 END), 0) AS [pending_count],
                ISNULL(SUM(CASE WHEN [status] = 'pending' AND ([next_attempt_at_utc] IS NULL OR [next_attempt_at_utc] <= SYSUTCDATETIME()) THEN 1 ELSE 0 END), 0) AS [ready_count],
                ISNULL(SUM(CASE WHEN [status] = 'pending' AND [next_attempt_at_utc] IS NOT NULL AND [next_attempt_at_utc] > SYSUTCDATETIME() THEN 1 ELSE 0 END), 0) AS [deferred_count],
                ISNULL(SUM(CASE WHEN [status] = 'published' THEN 1 ELSE 0 END), 0) AS [published_count],
                ISNULL(MAX([attempt_count]), 0) AS [max_attempt_count],
                MIN(CASE WHEN [status] = 'pending' THEN [created_at_utc] END) AS [oldest_pending_created_at_utc],
                MIN(CASE WHEN [status] = 'pending' AND [next_attempt_at_utc] IS NOT NULL AND [next_attempt_at_utc] > SYSUTCDATETIME() THEN [next_attempt_at_utc] END) AS [next_scheduled_attempt_at_utc]
            FROM [{_schema}].[outbox];
            """;

        await using var summaryCommand = new SqlCommand(summarySql, connection);
        await using var summaryReader = await summaryCommand.ExecuteReaderAsync(cancellationToken);
        await summaryReader.ReadAsync(cancellationToken);

        var totalCount = summaryReader.GetInt32(0);
        var pendingCount = summaryReader.GetInt32(1);
        var readyCount = summaryReader.GetInt32(2);
        var deferredCount = summaryReader.GetInt32(3);
        var publishedCount = summaryReader.GetInt32(4);
        var maxAttemptCount = summaryReader.GetInt32(5);
        var oldestPendingCreatedAtUtc = ReadNullableDateTimeOffset(summaryReader, 6);
        var nextScheduledAttemptAtUtc = ReadNullableDateTimeOffset(summaryReader, 7);

        var messageTypes = new List<OutboxMessageTypeDiagnosticsSnapshot>();
        await summaryReader.CloseAsync();

        var messageTypeSql = $"""
            SELECT
                [message_type],
                COUNT(*) AS [total_count],
                ISNULL(SUM(CASE WHEN [status] = 'pending' THEN 1 ELSE 0 END), 0) AS [pending_count],
                ISNULL(SUM(CASE WHEN [status] = 'pending' AND ([next_attempt_at_utc] IS NULL OR [next_attempt_at_utc] <= SYSUTCDATETIME()) THEN 1 ELSE 0 END), 0) AS [ready_count],
                ISNULL(SUM(CASE WHEN [status] = 'pending' AND [next_attempt_at_utc] IS NOT NULL AND [next_attempt_at_utc] > SYSUTCDATETIME() THEN 1 ELSE 0 END), 0) AS [deferred_count],
                ISNULL(SUM(CASE WHEN [status] = 'published' THEN 1 ELSE 0 END), 0) AS [published_count],
                ISNULL(MAX([attempt_count]), 0) AS [max_attempt_count]
            FROM [{_schema}].[outbox]
            GROUP BY [message_type]
            ORDER BY [pending_count] DESC, [total_count] DESC, [message_type] ASC;
            """;

        await using var messageTypeCommand = new SqlCommand(messageTypeSql, connection);
        await using var messageTypeReader = await messageTypeCommand.ExecuteReaderAsync(cancellationToken);
        while (await messageTypeReader.ReadAsync(cancellationToken))
        {
            messageTypes.Add(new OutboxMessageTypeDiagnosticsSnapshot(
                messageTypeReader.GetString(0),
                messageTypeReader.GetInt32(1),
                messageTypeReader.GetInt32(2),
                messageTypeReader.GetInt32(3),
                messageTypeReader.GetInt32(4),
                messageTypeReader.GetInt32(5),
                messageTypeReader.GetInt32(6)));
        }

        return new OutboxDiagnosticsSnapshot(
            totalCount,
            pendingCount,
            readyCount,
            deferredCount,
            publishedCount,
            maxAttemptCount,
            oldestPendingCreatedAtUtc,
            nextScheduledAttemptAtUtc,
            messageTypes);
    }

    private async Task<InboxDiagnosticsSnapshot> ReadInboxDiagnosticsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var summarySql = $"""
            SELECT ISNULL(COUNT(*), 0)
            FROM [{_schema}].[inbox]
            WHERE [status] = 'processed';
            """;

        await using var summaryCommand = new SqlCommand(summarySql, connection);
        var processedCount = Convert.ToInt32(await summaryCommand.ExecuteScalarAsync(cancellationToken));

        var consumerSql = $"""
            SELECT
                [consumer_name],
                COUNT(*) AS [processed_count],
                MAX([processed_at_utc]) AS [last_processed_at_utc]
            FROM [{_schema}].[inbox]
            WHERE [status] = 'processed'
            GROUP BY [consumer_name]
            ORDER BY [consumer_name] ASC;
            """;

        var consumers = new List<InboxConsumerDiagnosticsSnapshot>();
        await using var consumerCommand = new SqlCommand(consumerSql, connection);
        await using var consumerReader = await consumerCommand.ExecuteReaderAsync(cancellationToken);
        while (await consumerReader.ReadAsync(cancellationToken))
        {
            consumers.Add(new InboxConsumerDiagnosticsSnapshot(
                consumerReader.GetString(0),
                consumerReader.GetInt32(1),
                ReadNullableDateTimeOffset(consumerReader, 2)));
        }

        return new InboxDiagnosticsSnapshot(processedCount, consumers);
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return new DateTimeOffset(reader.GetDateTime(ordinal), TimeSpan.Zero);
    }

    private static string ValidateSchema(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (!Regex.IsMatch(schema, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw new ArgumentOutOfRangeException(nameof(schema), "SQL schema name contains unsupported characters.");
        }

        return schema;
    }
}

public sealed class DisabledBrokerDiagnosticsStore : IBrokerDiagnosticsStore
{
    private static readonly BrokerDiagnosticsSnapshot EmptySnapshot = new(
        false,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        Array.Empty<BrokerEndpointDiagnosticsSnapshot>());

    public bool IsAvailable => false;

    public Task<BrokerDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EmptySnapshot);
    }
}

public sealed class RabbitMqBrokerDiagnosticsStore(
    HttpClient httpClient,
    string virtualHost) : IBrokerDiagnosticsStore, IDisposable
{
    private static readonly BrokerDiagnosticsSnapshot UnavailableSnapshot = new(
        false,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        Array.Empty<BrokerEndpointDiagnosticsSnapshot>());

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly string _virtualHost = string.IsNullOrWhiteSpace(virtualHost)
        ? "/"
        : virtualHost;

    public bool IsAvailable => true;

    public async Task<BrokerDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"/api/queues/{Uri.EscapeDataString(_virtualHost)}", cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
            return BuildSnapshot(document.RootElement);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return UnavailableSnapshot;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static BrokerDiagnosticsSnapshot BuildSnapshot(JsonElement queuesElement)
    {
        if (queuesElement.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidOperationException("RabbitMQ management queues payload must be a JSON array.");
        }

        var endpoints = new Dictionary<string, MutableBrokerEndpointDiagnostics>(StringComparer.OrdinalIgnoreCase);
        var queueCount = 0;

        foreach (var queueElement in queuesElement.EnumerateArray())
        {
            var queueName = ReadString(queueElement, "name");
            if (string.IsNullOrWhiteSpace(queueName) || queueName.StartsWith("amq.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            queueCount += 1;

            var category = Classify(queueName);
            if (!endpoints.TryGetValue(category.EndpointName, out var endpoint))
            {
                endpoint = new MutableBrokerEndpointDiagnostics(category.EndpointName);
                endpoints.Add(category.EndpointName, endpoint);
            }

            var messageCount = ReadInt(queueElement, "messages");
            switch (category.Kind)
            {
                case BrokerQueueKind.Primary:
                    endpoint.QueueName = queueName;
                    endpoint.Status = ReadString(queueElement, "state", "unknown");
                    endpoint.ConsumerCount = ReadInt(queueElement, "consumers");
                    endpoint.MessageCount = messageCount;
                    endpoint.ReadyCount = ReadInt(queueElement, "messages_ready");
                    endpoint.UnackedCount = ReadInt(queueElement, "messages_unacknowledged");
                    endpoint.RedeliveryCount += ReadLong(queueElement, "message_stats", "redeliver");
                    break;
                case BrokerQueueKind.Fault:
                    endpoint.FaultCount += messageCount;
                    break;
                case BrokerQueueKind.DeadLetter:
                    endpoint.DeadLetterCount += messageCount;
                    break;
            }
        }

        var orderedEndpoints = endpoints.Values
            .OrderBy(endpoint => endpoint.EndpointName, StringComparer.OrdinalIgnoreCase)
            .Select(endpoint => endpoint.ToSnapshot())
            .ToArray();

        return new BrokerDiagnosticsSnapshot(
            true,
            queueCount,
            orderedEndpoints.Sum(endpoint => endpoint.ConsumerCount),
            orderedEndpoints.Sum(endpoint => endpoint.MessageCount),
            orderedEndpoints.Sum(endpoint => endpoint.ReadyCount),
            orderedEndpoints.Sum(endpoint => endpoint.UnackedCount),
            orderedEndpoints.Sum(endpoint => endpoint.FaultCount),
            orderedEndpoints.Sum(endpoint => endpoint.DeadLetterCount),
            orderedEndpoints.Sum(endpoint => endpoint.RedeliveryCount),
            orderedEndpoints);
    }

    private static (string EndpointName, BrokerQueueKind Kind) Classify(string queueName)
    {
        if (queueName.EndsWith("_error", StringComparison.OrdinalIgnoreCase))
        {
            return (queueName[..^"_error".Length], BrokerQueueKind.Fault);
        }

        if (queueName.EndsWith("_skipped", StringComparison.OrdinalIgnoreCase))
        {
            return (queueName[..^"_skipped".Length], BrokerQueueKind.DeadLetter);
        }

        return (queueName, BrokerQueueKind.Primary);
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var result) => result,
            JsonValueKind.Number when value.TryGetInt64(out var result) => Convert.ToInt32(result),
            _ => 0
        };
    }

    private static long ReadLong(JsonElement element, string objectPropertyName, string propertyName)
    {
        if (!element.TryGetProperty(objectPropertyName, out var nested) || nested.ValueKind is not JsonValueKind.Object)
        {
            return 0;
        }

        if (!nested.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var result) => result,
            JsonValueKind.Number when value.TryGetInt32(out var result) => result,
            _ => 0
        };
    }

    private static string ReadString(JsonElement element, string propertyName, string defaultValue = "")
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind is not JsonValueKind.String)
        {
            return defaultValue;
        }

        return value.GetString() ?? defaultValue;
    }

    private sealed class MutableBrokerEndpointDiagnostics(string endpointName)
    {
        public string EndpointName { get; } = endpointName;

        public string QueueName { get; set; } = endpointName;

        public string Status { get; set; } = "unknown";

        public int ConsumerCount { get; set; }

        public int MessageCount { get; set; }

        public int ReadyCount { get; set; }

        public int UnackedCount { get; set; }

        public int FaultCount { get; set; }

        public int DeadLetterCount { get; set; }

        public long RedeliveryCount { get; set; }

        public BrokerEndpointDiagnosticsSnapshot ToSnapshot() => new(
            EndpointName,
            QueueName,
            Status,
            ConsumerCount,
            MessageCount,
            ReadyCount,
            UnackedCount,
            FaultCount,
            DeadLetterCount,
            RedeliveryCount);
    }

    private enum BrokerQueueKind
    {
        Primary = 0,
        Fault = 1,
        DeadLetter = 2
    }
}
