using System.Security.Cryptography;
using System.Text;

namespace LawWatcher.AiEnrichment.Application;

public static class DocumentArtifactStorage
{
    public const string Bucket = "document-artifacts";
    public const string ExtractedTextKind = "extracted-text";
    public const string ExtractedTextContentType = "text/plain; charset=utf-8";

    public static string CreateExtractedTextObjectKey(
        string ownerType,
        Guid ownerId,
        string sourceBucket,
        string sourceObjectKey)
    {
        var normalizedOwnerType = NormalizeSegment(ownerType, nameof(ownerType));
        var normalizedSourceBucket = NormalizeSegment(sourceBucket, nameof(sourceBucket));
        var normalizedSourceObjectKey = NormalizeObjectKey(sourceObjectKey);
        var normalizedSourcePath = normalizedSourceObjectKey.Replace('\\', '/');
        var extension = Path.GetExtension(normalizedSourcePath);
        var withoutExtension = extension.Length == 0
            ? normalizedSourcePath
            : normalizedSourcePath[..^extension.Length];
        var sourceFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{normalizedSourceBucket}:{normalizedSourcePath}")))[..16];

        return $"{normalizedOwnerType}/{ownerId:D}/{sourceFingerprint}/{withoutExtension}.extracted.txt";
    }

    private static string NormalizeSegment(string value, string paramName)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Value cannot be empty.", paramName);
        }

        return normalized;
    }

    private static string NormalizeObjectKey(string value)
    {
        var normalized = value.Trim().Replace('\\', '/');
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Value cannot be empty.", nameof(value));
        }

        return normalized;
    }
}

public sealed record DocumentArtifactCatalogEntry(
    Guid ArtifactId,
    string OwnerType,
    Guid OwnerId,
    string SourceKind,
    string SourceBucket,
    string SourceObjectKey,
    string SourceContentType,
    string DerivedKind,
    string DerivedBucket,
    string DerivedObjectKey,
    string DerivedContentType,
    string ExtractedText,
    DateTimeOffset CreatedAtUtc);

public interface IDocumentArtifactCatalog
{
    Task UpsertAsync(DocumentArtifactCatalogEntry entry, CancellationToken cancellationToken);

    Task<DocumentArtifactCatalogEntry?> GetBySourceAsync(
        string sourceBucket,
        string sourceObjectKey,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<DocumentArtifactCatalogEntry>> ListAsync(CancellationToken cancellationToken);

    Task<int> DeleteAsync(IReadOnlyCollection<Guid> artifactIds, CancellationToken cancellationToken);
}
