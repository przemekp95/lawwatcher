using LawWatcher.BuildingBlocks.Ports;

namespace LawWatcher.AiEnrichment.Infrastructure;

public sealed class LocalFileDocumentStore(string rootDirectory) : IDocumentStore
{
    private readonly string _rootDirectory = NormalizeRoot(rootDirectory);

    public async Task<StoredDocumentReference> PutAsync(DocumentWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var bucket = NormalizeSegment(request.Bucket, nameof(request.Bucket), "Bucket");
        var objectKey = NormalizeObjectKey(request.ObjectKey);
        var contentType = NormalizeSegment(request.ContentType, nameof(request.ContentType), "Content type");

        var fullPath = GetFullPath(bucket, objectKey);
        var directoryPath = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Document store path must resolve to a directory.");
        Directory.CreateDirectory(directoryPath);

        request.Content.Position = 0;
        await using var destination = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        await request.Content.CopyToAsync(destination, cancellationToken);

        return new StoredDocumentReference(bucket, objectKey, contentType);
    }

    public Task<Stream> OpenReadAsync(StoredDocumentReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = GetFullPath(
            NormalizeSegment(reference.Bucket, nameof(reference.Bucket), "Bucket"),
            NormalizeObjectKey(reference.ObjectKey));

        Stream stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        return Task.FromResult(stream);
    }

    private string GetFullPath(string bucket, string objectKey)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, bucket, objectKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(_rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved document path escapes the configured root directory.");
        }

        return fullPath;
    }

    private static string NormalizeRoot(string rootDirectory)
    {
        var normalized = rootDirectory.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Document store root cannot be empty.", nameof(rootDirectory));
        }

        return Path.GetFullPath(normalized);
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
            throw new ArgumentOutOfRangeException(nameof(objectKey), "Object key must stay within the document store root.");
        }

        return normalized;
    }

    private static string NormalizeSegment(string value, string paramName, string label)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"{label} cannot be empty.", paramName);
        }

        return normalized;
    }
}
