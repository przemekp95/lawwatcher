using System.Text.Json;
using System.Text.RegularExpressions;
using LawWatcher.AiEnrichment.Application;
using Microsoft.Data.SqlClient;

namespace LawWatcher.AiEnrichment.Infrastructure;

public sealed class FileBackedDocumentArtifactCatalogStore(string rootDirectory) : IDocumentArtifactCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _rootDirectory = NormalizeRoot(rootDirectory);

    public async Task UpsertAsync(DocumentArtifactCatalogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        var metadataPath = GetMetadataPath(entry.SourceBucket, entry.SourceObjectKey);
        var metadataDirectory = Path.GetDirectoryName(metadataPath)
            ?? throw new InvalidOperationException("Document artifact metadata path must resolve to a directory.");
        Directory.CreateDirectory(metadataDirectory);

        var payload = JsonSerializer.Serialize(entry, SerializerOptions);
        await File.WriteAllTextAsync(metadataPath, payload, cancellationToken);
    }

    public async Task<DocumentArtifactCatalogEntry?> GetBySourceAsync(
        string sourceBucket,
        string sourceObjectKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var metadataPath = GetMetadataPath(sourceBucket, sourceObjectKey);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        var payload = await File.ReadAllTextAsync(metadataPath, cancellationToken);
        return JsonSerializer.Deserialize<DocumentArtifactCatalogEntry>(payload, SerializerOptions);
    }

    public async Task<IReadOnlyCollection<DocumentArtifactCatalogEntry>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = new List<DocumentArtifactCatalogEntry>();
        foreach (var metadataPath in Directory.EnumerateFiles(_rootDirectory, "*.json", SearchOption.AllDirectories))
        {
            var payload = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            var entry = JsonSerializer.Deserialize<DocumentArtifactCatalogEntry>(payload, SerializerOptions);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries
            .OrderByDescending(entry => entry.CreatedAtUtc)
            .ToArray();
    }

    public async Task<int> DeleteAsync(IReadOnlyCollection<Guid> artifactIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifactIds);
        cancellationToken.ThrowIfCancellationRequested();

        if (artifactIds.Count == 0 || !Directory.Exists(_rootDirectory))
        {
            return 0;
        }

        var ids = artifactIds.ToHashSet();
        var deletedCount = 0;

        foreach (var metadataPath in Directory.EnumerateFiles(_rootDirectory, "*.json", SearchOption.AllDirectories))
        {
            var payload = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            var entry = JsonSerializer.Deserialize<DocumentArtifactCatalogEntry>(payload, SerializerOptions);
            if (entry is null || !ids.Contains(entry.ArtifactId))
            {
                continue;
            }

            File.Delete(metadataPath);
            deletedCount++;
        }

        return deletedCount;
    }

    private string GetMetadataPath(string sourceBucket, string sourceObjectKey)
    {
        var bucket = NormalizeSegment(sourceBucket, nameof(sourceBucket));
        var objectKey = NormalizeObjectKey(sourceObjectKey);
        var path = Path.GetFullPath(Path.Combine(
            _rootDirectory,
            bucket,
            objectKey.Replace('/', Path.DirectorySeparatorChar) + ".json"));

        if (!path.StartsWith(_rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved document artifact path escapes the configured root directory.");
        }

        return path;
    }

    private static string NormalizeRoot(string rootDirectory)
    {
        var normalized = rootDirectory.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Document artifact root cannot be empty.", nameof(rootDirectory));
        }

        Directory.CreateDirectory(normalized);
        return Path.GetFullPath(normalized);
    }

    private static string NormalizeSegment(string value, string paramName)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Value cannot be empty.", paramName);
        }

        return normalized;
    }

    private static string NormalizeObjectKey(string objectKey)
    {
        var normalized = objectKey.Trim().Replace('\\', '/');
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Object key cannot be empty.", nameof(objectKey));
        }

        if (normalized.StartsWith('/') || normalized.Contains("../", StringComparison.Ordinal) || normalized.Contains("..\\", StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(objectKey), "Object key must stay within the document artifact root.");
        }

        return normalized;
    }
}

public sealed class SqlServerDocumentArtifactCatalogStore(
    string connectionString,
    string schema = "lawwatcher") : IDocumentArtifactCatalog
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString))
        : connectionString;
    private readonly string _schema = ValidateSchema(schema);

    public async Task UpsertAsync(DocumentArtifactCatalogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var sql = $"""
            MERGE [{_schema}].[document_artifacts] AS [target]
            USING (VALUES
                (
                    @artifactId,
                    @ownerType,
                    @ownerId,
                    @sourceKind,
                    @sourceBucket,
                    @sourceObjectKey,
                    @sourceContentType,
                    @derivedKind,
                    @derivedBucket,
                    @derivedObjectKey,
                    @derivedContentType,
                    @extractedText,
                    @createdAtUtc
                )
            ) AS [source]
            (
                [artifact_id],
                [owner_type],
                [owner_id],
                [source_kind],
                [source_bucket],
                [source_object_key],
                [source_content_type],
                [derived_kind],
                [derived_bucket],
                [derived_object_key],
                [derived_content_type],
                [extracted_text],
                [created_at_utc]
            )
            ON [target].[source_bucket] = [source].[source_bucket]
               AND [target].[source_object_key] = [source].[source_object_key]
            WHEN MATCHED THEN
                UPDATE SET
                    [owner_type] = [source].[owner_type],
                    [owner_id] = [source].[owner_id],
                    [source_kind] = [source].[source_kind],
                    [source_content_type] = [source].[source_content_type],
                    [derived_kind] = [source].[derived_kind],
                    [derived_bucket] = [source].[derived_bucket],
                    [derived_object_key] = [source].[derived_object_key],
                    [derived_content_type] = [source].[derived_content_type],
                    [extracted_text] = [source].[extracted_text],
                    [created_at_utc] = [source].[created_at_utc]
            WHEN NOT MATCHED THEN
                INSERT
                (
                    [artifact_id],
                    [owner_type],
                    [owner_id],
                    [source_kind],
                    [source_bucket],
                    [source_object_key],
                    [source_content_type],
                    [derived_kind],
                    [derived_bucket],
                    [derived_object_key],
                    [derived_content_type],
                    [extracted_text],
                    [created_at_utc]
                )
                VALUES
                (
                    [source].[artifact_id],
                    [source].[owner_type],
                    [source].[owner_id],
                    [source].[source_kind],
                    [source].[source_bucket],
                    [source].[source_object_key],
                    [source].[source_content_type],
                    [source].[derived_kind],
                    [source].[derived_bucket],
                    [source].[derived_object_key],
                    [source].[derived_content_type],
                    [source].[extracted_text],
                    [source].[created_at_utc]
                );
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        PopulateParameters(command, entry);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DocumentArtifactCatalogEntry?> GetBySourceAsync(
        string sourceBucket,
        string sourceObjectKey,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT
                [artifact_id],
                [owner_type],
                [owner_id],
                [source_kind],
                [source_bucket],
                [source_object_key],
                [source_content_type],
                [derived_kind],
                [derived_bucket],
                [derived_object_key],
                [derived_content_type],
                [extracted_text],
                [created_at_utc]
            FROM [{_schema}].[document_artifacts]
            WHERE [source_bucket] = @sourceBucket
              AND [source_object_key] = @sourceObjectKey;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceBucket", sourceBucket.Trim());
        command.Parameters.AddWithValue("@sourceObjectKey", sourceObjectKey.Trim().Replace('\\', '/'));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ToDomain(reader);
    }

    public async Task<IReadOnlyCollection<DocumentArtifactCatalogEntry>> ListAsync(CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT
                [artifact_id],
                [owner_type],
                [owner_id],
                [source_kind],
                [source_bucket],
                [source_object_key],
                [source_content_type],
                [derived_kind],
                [derived_bucket],
                [derived_object_key],
                [derived_content_type],
                [extracted_text],
                [created_at_utc]
            FROM [{_schema}].[document_artifacts]
            ORDER BY [created_at_utc] DESC, [owner_type] ASC, [owner_id] ASC;
            """;

        var entries = new List<DocumentArtifactCatalogEntry>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(ToDomain(reader));
        }

        return entries;
    }

    public async Task<int> DeleteAsync(IReadOnlyCollection<Guid> artifactIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifactIds);

        var ids = artifactIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        var parameterNames = ids
            .Select((_, index) => $"@artifactId{index}")
            .ToArray();

        var sql = $"""
            DELETE FROM [{_schema}].[document_artifacts]
            WHERE [artifact_id] IN ({string.Join(", ", parameterNames)});
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        for (var index = 0; index < ids.Length; index++)
        {
            command.Parameters.AddWithValue(parameterNames[index], ids[index]);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void PopulateParameters(SqlCommand command, DocumentArtifactCatalogEntry entry)
    {
        command.Parameters.AddWithValue("@artifactId", entry.ArtifactId);
        command.Parameters.AddWithValue("@ownerType", entry.OwnerType);
        command.Parameters.AddWithValue("@ownerId", entry.OwnerId);
        command.Parameters.AddWithValue("@sourceKind", entry.SourceKind);
        command.Parameters.AddWithValue("@sourceBucket", entry.SourceBucket);
        command.Parameters.AddWithValue("@sourceObjectKey", entry.SourceObjectKey);
        command.Parameters.AddWithValue("@sourceContentType", entry.SourceContentType);
        command.Parameters.AddWithValue("@derivedKind", entry.DerivedKind);
        command.Parameters.AddWithValue("@derivedBucket", entry.DerivedBucket);
        command.Parameters.AddWithValue("@derivedObjectKey", entry.DerivedObjectKey);
        command.Parameters.AddWithValue("@derivedContentType", entry.DerivedContentType);
        command.Parameters.AddWithValue("@extractedText", entry.ExtractedText);
        command.Parameters.AddWithValue("@createdAtUtc", entry.CreatedAtUtc);
    }

    private static DocumentArtifactCatalogEntry ToDomain(SqlDataReader reader)
    {
        return new DocumentArtifactCatalogEntry(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            ReadDateTimeOffset(reader, 12));
    }

    private static DateTimeOffset ReadDateTimeOffset(SqlDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc), TimeSpan.Zero),
            _ => throw new InvalidOperationException(
                $"Document artifact timestamp column at ordinal {ordinal} must be a DateTimeOffset-compatible value.")
        };
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
