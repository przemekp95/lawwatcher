using System.Text;
using LawWatcher.AiEnrichment.Application;
using LawWatcher.AiEnrichment.Domain.Tasks;
using LawWatcher.BuildingBlocks.Ports;
using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegalCorpus.Domain.Acts;

namespace LawWatcher.AiEnrichment.Infrastructure;

public sealed class PublishedActAiPromptAugmentor(
    IPublishedActRepository actRepository,
    IDocumentArtifactCatalog artifactCatalog) : IAiPromptAugmentor
{
    private const int MaxExtractedCharacters = 1600;

    public async Task<AiPromptAugmentation> AugmentAsync(
        AiTaskSubject subject,
        string prompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (!string.Equals(subject.Type, "act", StringComparison.OrdinalIgnoreCase))
        {
            return new AiPromptAugmentation(prompt, []);
        }

        var act = await actRepository.GetAsync(new ActId(subject.Id), cancellationToken);
        if (act is null)
        {
            return new AiPromptAugmentation(prompt, []);
        }

        var artifact = act.Artifacts
            .OrderByDescending(item => string.Equals(item.Kind, "text", StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.ObjectKey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (artifact is null)
        {
            return new AiPromptAugmentation(prompt, [act.Eli.Value]);
        }

        var extractedArtifact = await artifactCatalog.GetBySourceAsync(
            LegalCorpusArtifactStorage.Bucket,
            artifact.ObjectKey,
            cancellationToken);
        if (extractedArtifact is null)
        {
            throw new DerivedDocumentTextNotReadyException(artifact.ObjectKey);
        }

        var excerpt = NormalizeExtractedText(extractedArtifact.ExtractedText);
        if (excerpt.Length == 0)
        {
            return new AiPromptAugmentation(prompt, [act.Eli.Value, LegalCorpusArtifactStorage.CreateCitation(artifact.ObjectKey)]);
        }

        var groundedPrompt = new StringBuilder(prompt.Length + excerpt.Length + 256)
            .AppendLine(prompt)
            .AppendLine()
            .AppendLine("Material z dokumentu zrodlowego:")
            .Append("- ELI: ").AppendLine(act.Eli.Value)
            .Append("- Artefakt: ").AppendLine(LegalCorpusArtifactStorage.CreateCitation(artifact.ObjectKey))
            .AppendLine("Wyciag tekstu ze zrodla:")
            .Append(excerpt)
            .ToString();

        return new AiPromptAugmentation(
            groundedPrompt,
            [act.Eli.Value, LegalCorpusArtifactStorage.CreateCitation(artifact.ObjectKey)]);
    }

    private static string NormalizeExtractedText(string extractedText)
    {
        var normalized = string.Join(
            " ",
            extractedText
                .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (normalized.Length <= MaxExtractedCharacters)
        {
            return normalized;
        }

        return normalized[..MaxExtractedCharacters];
    }
}
