using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegalCorpus.Domain.Acts;

namespace LawWatcher.Worker.Documents;

public sealed record DocumentArtifactCatchUpResult(int ProcessedCount);

public sealed class DocumentArtifactCatchUpService(
    ILogger<DocumentArtifactCatchUpService> logger,
    ActsQueryService actsQueryService,
    IPublishedActRepository actRepository,
    DocumentProcessingService processingService)
{
    public async Task<DocumentArtifactCatchUpResult> ProcessPendingActsAsync(CancellationToken cancellationToken)
    {
        var acts = await actsQueryService.GetActsAsync(cancellationToken);
        var processedCount = 0;

        foreach (var actSummary in acts.Where(static act => act.ArtifactKinds.Contains("text", StringComparer.OrdinalIgnoreCase)))
        {
            var act = await actRepository.GetAsync(new ActId(actSummary.Id), cancellationToken);
            if (act is null)
            {
                continue;
            }

            foreach (var artifact in act.Artifacts.Where(static artifact => string.Equals(artifact.Kind, "text", StringComparison.OrdinalIgnoreCase)))
            {
                var result = await processingService.ProcessActArtifactAsync(
                    actSummary.Id,
                    artifact.Kind,
                    artifact.ObjectKey,
                    cancellationToken);
                logger.LogInformation(
                    "worker-documents startup catch-up handled. flow=document-ocr ownerType={OwnerType} ownerId={OwnerId} sourceKind={SourceKind} sourceObjectKey={SourceObjectKey} derivedObjectKey={DerivedObjectKey} hasProcessedDocument={HasProcessedDocument} wasAlreadyProcessed={WasAlreadyProcessed} warningCount={WarningCount}",
                    result.OwnerType,
                    result.OwnerId,
                    result.SourceKind,
                    result.SourceObjectKey,
                    result.DerivedObjectKey,
                    result.HasProcessedDocument,
                    result.WasAlreadyProcessed,
                    result.WarningCount);
                if (result.HasProcessedDocument)
                {
                    processedCount++;
                }
            }
        }

        return new DocumentArtifactCatchUpResult(processedCount);
    }
}

public sealed class DocumentArtifactCatchUpHostedService(
    ILogger<DocumentArtifactCatchUpHostedService> logger,
    DocumentArtifactCatchUpService catchUpService) : BackgroundService
{
    private const int PassLimit = 6;
    private static readonly TimeSpan PassDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var pass = 1; pass <= PassLimit && !stoppingToken.IsCancellationRequested; pass++)
        {
            var result = await catchUpService.ProcessPendingActsAsync(stoppingToken);
            logger.LogInformation(
                "worker-documents startup catch-up pass completed. pass={Pass} passLimit={PassLimit} processedArtifacts={ProcessedArtifacts}",
                pass,
                PassLimit,
                result.ProcessedCount);

            if (pass < PassLimit)
            {
                await Task.Delay(PassDelay, stoppingToken);
            }
        }
    }
}
