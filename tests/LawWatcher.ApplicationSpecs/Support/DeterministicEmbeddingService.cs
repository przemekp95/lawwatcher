using LawWatcher.BuildingBlocks.Ports;

namespace LawWatcher.AiEnrichment.Infrastructure;

public sealed class DeterministicEmbeddingService(string model = "lawwatcher-local-embedding-v1") : IEmbeddingService
{
    public Task<EmbeddingVector> GenerateAsync(string content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = NormalizeContent(content);
        var vector = new float[8];

        for (var index = 0; index < normalized.Length; index++)
        {
            var slot = index % vector.Length;
            vector[slot] += normalized[index] / 255F;
        }

        return Task.FromResult(new EmbeddingVector(model, vector));
    }

    private static string NormalizeContent(string content)
    {
        var normalized = content.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Embedding content cannot be empty.", nameof(content));
        }

        return normalized.ToUpperInvariant();
    }
}
