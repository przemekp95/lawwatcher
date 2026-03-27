namespace LawWatcher.BuildingBlocks.Configuration;

public enum DocumentStoreBackend
{
    LocalFiles = 0,
    S3Compatible = 1
}

public sealed class ObjectStorageOptions
{
    public string LocalDocumentsRoot { get; init; } = Path.Combine("artifacts", "documents");

    public S3CompatibleDocumentStoreOptions Minio { get; init; } = new();
}

public sealed class S3CompatibleDocumentStoreOptions
{
    public string Endpoint { get; init; } = string.Empty;

    public string AccessKey { get; init; } = string.Empty;

    public string SecretKey { get; init; } = string.Empty;

    public string Region { get; init; } = "us-east-1";

    public bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(AccessKey)
        && !string.IsNullOrWhiteSpace(SecretKey);
}

public static class DocumentStoreRuntimeResolver
{
    public static DocumentStoreBackend Select(ObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Minio.IsConfigured()
            ? DocumentStoreBackend.S3Compatible
            : DocumentStoreBackend.LocalFiles;
    }

    public static string ResolveLocalDocumentsRoot(ObjectStorageOptions options, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var configuredRoot = string.IsNullOrWhiteSpace(options.LocalDocumentsRoot)
            ? Path.Combine("artifacts", "documents")
            : options.LocalDocumentsRoot;

        var combinedPath = Path.IsPathRooted(configuredRoot)
            ? configuredRoot
            : Path.Combine(contentRootPath, configuredRoot);

        return Path.GetFullPath(combinedPath);
    }
}
