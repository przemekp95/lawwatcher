using LawWatcher.BuildingBlocks.Ports;

namespace LawWatcher.LegislativeIntake.Application;

public static class LegislativeIntakeDocumentStorage
{
    public const string Bucket = "legislative-intake";

    public static StoredDocumentReference CreateDocumentReference(string objectKey)
    {
        var normalizedObjectKey = NormalizeObjectKey(objectKey);
        return new StoredDocumentReference(
            Bucket,
            normalizedObjectKey,
            GuessContentType(normalizedObjectKey));
    }

    public static string CreateCitation(string objectKey) =>
        $"document://{Bucket}/{NormalizeObjectKey(objectKey)}";

    public static string GuessContentType(string objectKey)
    {
        var extension = Path.GetExtension(NormalizeObjectKey(objectKey));
        return extension.ToLowerInvariant() switch
        {
            ".txt" => "text/plain; charset=utf-8",
            ".md" => "text/markdown; charset=utf-8",
            ".html" => "text/html; charset=utf-8",
            ".json" => "application/json",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };
    }

    private static string NormalizeObjectKey(string objectKey)
    {
        var normalized = objectKey.Trim().Replace('\\', '/');
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Object key cannot be empty.", nameof(objectKey));
        }

        return normalized;
    }
}
