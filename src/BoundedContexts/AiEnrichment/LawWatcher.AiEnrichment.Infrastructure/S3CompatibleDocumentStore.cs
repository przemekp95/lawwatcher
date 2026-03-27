using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.BuildingBlocks.Ports;
using Minio;
using Minio.DataModel.Args;

namespace LawWatcher.AiEnrichment.Infrastructure;

public sealed class S3CompatibleDocumentStore(S3CompatibleDocumentStoreOptions options) : IDocumentStore
{
    private readonly S3CompatibleDocumentStoreOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IMinioClient _client = CreateClient(options);

    public async Task<StoredDocumentReference> PutAsync(DocumentWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var bucket = NormalizeSegment(request.Bucket, nameof(request.Bucket), "Bucket");
        var objectKey = NormalizeObjectKey(request.ObjectKey);
        var contentType = NormalizeSegment(request.ContentType, nameof(request.ContentType), "Content type");

        await EnsureBucketExistsAsync(bucket, cancellationToken);

        await using var bufferedContent = new MemoryStream();
        request.Content.Position = 0;
        await request.Content.CopyToAsync(bufferedContent, cancellationToken);
        bufferedContent.Position = 0;

        var putObjectArgs = new PutObjectArgs()
            .WithBucket(bucket)
            .WithObject(objectKey)
            .WithObjectSize(bufferedContent.Length)
            .WithStreamData(bufferedContent)
            .WithContentType(contentType);

        await _client.PutObjectAsync(putObjectArgs, cancellationToken);
        return new StoredDocumentReference(bucket, objectKey, contentType);
    }

    public async Task<Stream> OpenReadAsync(StoredDocumentReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();

        var bucket = NormalizeSegment(reference.Bucket, nameof(reference.Bucket), "Bucket");
        var objectKey = NormalizeObjectKey(reference.ObjectKey);

        var content = new MemoryStream();
        var getObjectArgs = new GetObjectArgs()
            .WithBucket(bucket)
            .WithObject(objectKey)
            .WithCallbackStream(stream =>
            {
                stream.CopyTo(content);
            });

        await _client.GetObjectAsync(getObjectArgs, cancellationToken);
        content.Position = 0;
        return content;
    }

    public async Task DeleteAsync(StoredDocumentReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();

        var bucket = NormalizeSegment(reference.Bucket, nameof(reference.Bucket), "Bucket");
        var objectKey = NormalizeObjectKey(reference.ObjectKey);

        try
        {
            await _client.RemoveObjectAsync(
                new RemoveObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(objectKey),
                cancellationToken);
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            // Retention treats already-missing derived artifacts as successfully removed.
        }
    }

    private async Task EnsureBucketExistsAsync(string bucket, CancellationToken cancellationToken)
    {
        var bucketExists = await _client.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(bucket),
            cancellationToken);

        if (!bucketExists)
        {
            try
            {
                await _client.MakeBucketAsync(
                    new MakeBucketArgs()
                        .WithBucket(bucket),
                    cancellationToken);
            }
            catch (ArgumentException)
            {
                if (!await _client.BucketExistsAsync(
                    new BucketExistsArgs().WithBucket(bucket),
                    cancellationToken))
                {
                    throw;
                }

                // Another worker created the bucket between the existence check and create call.
            }
        }
    }

    private static IMinioClient CreateClient(S3CompatibleDocumentStoreOptions options)
    {
        if (!options.IsConfigured())
        {
            throw new InvalidOperationException("S3-compatible document store options must include endpoint, access key and secret key.");
        }

        var endpoint = new Uri(options.Endpoint.Trim(), UriKind.Absolute);

        return new MinioClient()
            .WithEndpoint(endpoint.Authority)
            .WithCredentials(options.AccessKey.Trim(), options.SecretKey.Trim())
            .WithSSL(string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            .WithRegion(options.Region.Trim())
            .Build();
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
            throw new ArgumentOutOfRangeException(nameof(objectKey), "Object key must stay within the configured document store namespace.");
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
