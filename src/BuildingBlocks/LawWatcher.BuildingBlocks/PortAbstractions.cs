namespace LawWatcher.BuildingBlocks.Ports;

public sealed record DocumentWriteRequest(
    string Bucket,
    string ObjectKey,
    string ContentType,
    Stream Content);

public sealed record StoredDocumentReference(
    string Bucket,
    string ObjectKey,
    string ContentType);

public sealed record OcrResult(
    string ExtractedText,
    IReadOnlyCollection<string> Warnings);

public sealed record LlmCompletion(
    string Model,
    string Content,
    IReadOnlyCollection<string> Citations);

public sealed record EmbeddingVector(
    string Model,
    IReadOnlyCollection<float> Values);

public sealed record WebhookDispatchRequest(
    string CallbackUrl,
    string EventType,
    string Payload,
    IReadOnlyDictionary<string, string> Headers);

public sealed record NotificationDispatchRequest(
    string Recipient,
    string Subject,
    string Content,
    string EventType,
    IReadOnlyDictionary<string, string> Metadata);

public interface IDocumentStore
{
    Task<StoredDocumentReference> PutAsync(DocumentWriteRequest request, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(StoredDocumentReference reference, CancellationToken cancellationToken);

    Task DeleteAsync(StoredDocumentReference reference, CancellationToken cancellationToken);
}

public interface IOcrService
{
    Task<OcrResult> ExtractAsync(StoredDocumentReference document, CancellationToken cancellationToken);
}

public interface ILlmService
{
    Task<LlmCompletion> CompleteAsync(string prompt, CancellationToken cancellationToken);
}

public interface IEmbeddingService
{
    Task<EmbeddingVector> GenerateAsync(string content, CancellationToken cancellationToken);
}

public interface ISearchIndexer
{
    Task IndexAsync(string documentId, string title, string content, CancellationToken cancellationToken);

    Task RemoveAsync(string documentId, CancellationToken cancellationToken);
}

public interface IWebhookDispatcher
{
    Task DispatchAsync(WebhookDispatchRequest request, CancellationToken cancellationToken);
}

public interface INotificationChannel
{
    string ChannelCode { get; }

    Task DispatchAsync(NotificationDispatchRequest request, CancellationToken cancellationToken);
}
