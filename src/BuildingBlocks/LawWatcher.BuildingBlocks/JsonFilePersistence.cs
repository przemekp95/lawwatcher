using System.Collections.Concurrent;
using System.Text.Json;

namespace LawWatcher.BuildingBlocks.Persistence;

public static class JsonFilePersistence
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task ExecuteLockedAsync(
        string lockKey,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockKey);
        ArgumentNullException.ThrowIfNull(action);

        var gate = Gates.GetOrAdd(Path.GetFullPath(lockKey), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            await action(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<T> ExecuteLockedAsync<T>(
        string lockKey,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockKey);
        ArgumentNullException.ThrowIfNull(action);

        var gate = Gates.GetOrAdd(Path.GetFullPath(lockKey), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<T> LoadAsync<T>(
        string path,
        Func<T> createDefault,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(createDefault);

        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(path))
        {
            return createDefault();
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken);
        return value ?? createDefault();
    }

    public static async Task SaveAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Path '{path}' does not contain a parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        await ReplaceFileWithRetryAsync(temporaryPath, path, cancellationToken);
    }

    private static async Task ReplaceFileWithRetryAsync(
        string temporaryPath,
        string path,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }

        if (File.Exists(path))
        {
            File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }

        File.Move(temporaryPath, path);
    }
}
