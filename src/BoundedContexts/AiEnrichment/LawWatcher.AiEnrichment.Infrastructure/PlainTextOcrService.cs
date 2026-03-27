using System.Text;
using LawWatcher.BuildingBlocks.Ports;

namespace LawWatcher.AiEnrichment.Infrastructure;

public sealed class PlainTextOcrService(IDocumentStore documentStore) : IOcrService
{
    public async Task<OcrResult> ExtractAsync(StoredDocumentReference document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        await using var content = await documentStore.OpenReadAsync(document, cancellationToken);
        using var reader = new StreamReader(
            content,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);

        var extractedText = await reader.ReadToEndAsync(cancellationToken);
        var warnings = document.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            ? Array.Empty<string>()
            : ["Content type is not text/*; OCR adapter treated the document as UTF-8 text."];

        return new OcrResult(extractedText, warnings);
    }
}
