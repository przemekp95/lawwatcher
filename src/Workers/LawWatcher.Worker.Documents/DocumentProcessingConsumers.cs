using System.Text;
using LawWatcher.AiEnrichment.Application;
using LawWatcher.AiEnrichment.Contracts;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.BuildingBlocks.Ports;
using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegalCorpus.Contracts;
using LawWatcher.LegislativeIntake.Application;
using LawWatcher.LegislativeIntake.Contracts;
using MassTransit;

namespace LawWatcher.Worker.Documents;

public sealed record DocumentProcessingResult(
    bool HasProcessedDocument,
    bool WasAlreadyProcessed,
    string OwnerType,
    Guid OwnerId,
    string SourceKind,
    string SourceObjectKey,
    string DerivedObjectKey,
    int WarningCount);

public sealed class DocumentProcessingService(
    IDocumentStore documentStore,
    IOcrService ocrService,
    IDocumentArtifactCatalog artifactCatalog,
    IIntegrationEventPublisher integrationEventPublisher)
{
    public Task<DocumentProcessingResult> ProcessActArtifactAsync(
        Guid actId,
        string kind,
        string objectKey,
        CancellationToken cancellationToken)
    {
        return ProcessCoreAsync(
            ownerType: "act",
            ownerId: actId,
            sourceKind: kind,
            sourceDocument: LegalCorpusArtifactStorage.CreateDocumentReference(objectKey),
            cancellationToken);
    }

    public Task<DocumentProcessingResult> ProcessAsync(
        ActArtifactAttachedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return ProcessActArtifactAsync(
            integrationEvent.ActId,
            integrationEvent.Kind,
            integrationEvent.ObjectKey,
            cancellationToken);
    }

    public Task<DocumentProcessingResult> ProcessAsync(
        BillDocumentAttachedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return ProcessCoreAsync(
            ownerType: "bill",
            ownerId: integrationEvent.BillId,
            sourceKind: integrationEvent.Kind,
            sourceDocument: LegislativeIntakeDocumentStorage.CreateDocumentReference(integrationEvent.ObjectKey),
            cancellationToken);
    }

    private async Task<DocumentProcessingResult> ProcessCoreAsync(
        string ownerType,
        Guid ownerId,
        string sourceKind,
        StoredDocumentReference sourceDocument,
        CancellationToken cancellationToken)
    {
        var existingArtifact = await artifactCatalog.GetBySourceAsync(
            sourceDocument.Bucket,
            sourceDocument.ObjectKey,
            cancellationToken);
        if (existingArtifact is not null)
        {
            await PublishDocumentTextExtractedAsync(
                ownerType,
                ownerId,
                sourceKind,
                sourceDocument,
                existingArtifact.DerivedBucket,
                existingArtifact.DerivedObjectKey,
                cancellationToken);

            return new DocumentProcessingResult(
                HasProcessedDocument: false,
                WasAlreadyProcessed: true,
                ownerType,
                ownerId,
                sourceKind,
                sourceDocument.ObjectKey,
                existingArtifact.DerivedObjectKey,
                WarningCount: 0);
        }

        var ocrResult = await ocrService.ExtractAsync(sourceDocument, cancellationToken);
        var extractedText = ocrResult.ExtractedText.Trim();
        var derivedObjectKey = DocumentArtifactStorage.CreateExtractedTextObjectKey(
            ownerType,
            ownerId,
            sourceDocument.Bucket,
            sourceDocument.ObjectKey);
        var bytes = Encoding.UTF8.GetBytes(extractedText);
        await using var content = new MemoryStream(bytes, writable: false);
        var storedDerivedDocument = await documentStore.PutAsync(
            new DocumentWriteRequest(
                DocumentArtifactStorage.Bucket,
                derivedObjectKey,
                DocumentArtifactStorage.ExtractedTextContentType,
                content),
            cancellationToken);

        var entry = new DocumentArtifactCatalogEntry(
            Guid.NewGuid(),
            ownerType,
            ownerId,
            sourceKind,
            sourceDocument.Bucket,
            sourceDocument.ObjectKey,
            sourceDocument.ContentType,
            DocumentArtifactStorage.ExtractedTextKind,
            storedDerivedDocument.Bucket,
            storedDerivedDocument.ObjectKey,
            storedDerivedDocument.ContentType,
            extractedText,
            DateTimeOffset.UtcNow);
        await artifactCatalog.UpsertAsync(entry, cancellationToken);
        await PublishDocumentTextExtractedAsync(
            ownerType,
            ownerId,
            sourceKind,
            sourceDocument,
            storedDerivedDocument.Bucket,
            storedDerivedDocument.ObjectKey,
            cancellationToken);

        return new DocumentProcessingResult(
            HasProcessedDocument: true,
            WasAlreadyProcessed: false,
            ownerType,
            ownerId,
            sourceKind,
            sourceDocument.ObjectKey,
            storedDerivedDocument.ObjectKey,
            ocrResult.Warnings.Count);
    }

    private Task PublishDocumentTextExtractedAsync(
        string ownerType,
        Guid ownerId,
        string sourceKind,
        StoredDocumentReference sourceDocument,
        string derivedBucket,
        string derivedObjectKey,
        CancellationToken cancellationToken)
    {
        return integrationEventPublisher.PublishAsync(
            new DocumentTextExtractedIntegrationEvent(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                ownerType,
                ownerId,
                sourceKind,
                sourceDocument.Bucket,
                sourceDocument.ObjectKey,
                derivedBucket,
                derivedObjectKey),
            cancellationToken);
    }
}

public sealed class DocumentProcessingMessageHandler(
    DocumentProcessingService processingService,
    IInboxStore inboxStore)
{
    public const string ConsumerName = "worker-documents.document-processing";

    public async Task<DocumentProcessingResult> HandleAsync(
        ActArtifactAttachedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (await inboxStore.HasProcessedAsync(integrationEvent.EventId, ConsumerName, cancellationToken))
        {
            return new DocumentProcessingResult(
                HasProcessedDocument: false,
                WasAlreadyProcessed: true,
                "act",
                integrationEvent.ActId,
                integrationEvent.Kind,
                integrationEvent.ObjectKey,
                string.Empty,
                WarningCount: 0);
        }

        var result = await processingService.ProcessAsync(integrationEvent, cancellationToken);
        await inboxStore.MarkProcessedAsync(integrationEvent.EventId, ConsumerName, cancellationToken);
        return result;
    }

    public async Task<DocumentProcessingResult> HandleAsync(
        BillDocumentAttachedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (await inboxStore.HasProcessedAsync(integrationEvent.EventId, ConsumerName, cancellationToken))
        {
            return new DocumentProcessingResult(
                HasProcessedDocument: false,
                WasAlreadyProcessed: true,
                "bill",
                integrationEvent.BillId,
                integrationEvent.Kind,
                integrationEvent.ObjectKey,
                string.Empty,
                WarningCount: 0);
        }

        var result = await processingService.ProcessAsync(integrationEvent, cancellationToken);
        await inboxStore.MarkProcessedAsync(integrationEvent.EventId, ConsumerName, cancellationToken);
        return result;
    }
}

public sealed class ActArtifactAttachedConsumer(
    ILogger<ActArtifactAttachedConsumer> logger,
    DocumentProcessingMessageHandler messageHandler) : IConsumer<ActArtifactAttachedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<ActArtifactAttachedIntegrationEvent> context)
    {
        var result = await messageHandler.HandleAsync(context.Message, context.CancellationToken);
        logger.LogInformation(
            "worker-documents broker message handled. flow=document-ocr ownerType={OwnerType} ownerId={OwnerId} sourceKind={SourceKind} sourceObjectKey={SourceObjectKey} derivedObjectKey={DerivedObjectKey} hasProcessedDocument={HasProcessedDocument} wasAlreadyProcessed={WasAlreadyProcessed} warningCount={WarningCount}",
            result.OwnerType,
            result.OwnerId,
            result.SourceKind,
            result.SourceObjectKey,
            result.DerivedObjectKey,
            result.HasProcessedDocument,
            result.WasAlreadyProcessed,
            result.WarningCount);
    }
}

public sealed class BillDocumentAttachedConsumer(
    ILogger<BillDocumentAttachedConsumer> logger,
    DocumentProcessingMessageHandler messageHandler) : IConsumer<BillDocumentAttachedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<BillDocumentAttachedIntegrationEvent> context)
    {
        var result = await messageHandler.HandleAsync(context.Message, context.CancellationToken);
        logger.LogInformation(
            "worker-documents broker message handled. flow=document-ocr ownerType={OwnerType} ownerId={OwnerId} sourceKind={SourceKind} sourceObjectKey={SourceObjectKey} derivedObjectKey={DerivedObjectKey} hasProcessedDocument={HasProcessedDocument} wasAlreadyProcessed={WasAlreadyProcessed} warningCount={WarningCount}",
            result.OwnerType,
            result.OwnerId,
            result.SourceKind,
            result.SourceObjectKey,
            result.DerivedObjectKey,
            result.HasProcessedDocument,
            result.WasAlreadyProcessed,
            result.WarningCount);
    }
}
