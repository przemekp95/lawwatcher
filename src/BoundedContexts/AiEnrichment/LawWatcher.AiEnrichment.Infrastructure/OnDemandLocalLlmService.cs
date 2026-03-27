using System.Text.RegularExpressions;
using LawWatcher.BuildingBlocks.Ports;

namespace LawWatcher.AiEnrichment.Infrastructure;

public sealed partial class OnDemandLocalLlmService(string model = "llama3.2:1b") : ILlmService
{
    public Task<LlmCompletion> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPrompt = NormalizePrompt(prompt);
        var citations = UrlRegex()
            .Matches(normalizedPrompt)
            .Select(match => TrimTrailingPunctuation(match.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var summary = normalizedPrompt.Length <= 180
            ? normalizedPrompt
            : $"{normalizedPrompt[..177]}...";

        return Task.FromResult(new LlmCompletion(
            model,
            $"Streszczenie lokalne: {summary}",
            citations));
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    private static string NormalizePrompt(string prompt)
    {
        var normalized = string.Join(" ", prompt
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (normalized.Length == 0)
        {
            throw new ArgumentException("Prompt cannot be empty.", nameof(prompt));
        }

        return normalized;
    }

    private static string TrimTrailingPunctuation(string value) =>
        value.TrimEnd('.', ',', ';', ':', ')', ']');
}
