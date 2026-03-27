using Microsoft.Extensions.Configuration;
using System.Collections;

namespace LawWatcher.BuildingBlocks.Configuration;

public readonly record struct RuntimeProfile(string Value)
{
    public static RuntimeProfile DevLaptop => new("dev-laptop");

    public static RuntimeProfile FullHost => new("full-host");

    public static RuntimeProfile Parse(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "dev-laptop" => DevLaptop,
            "full-host" => FullHost,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported runtime profile.")
        };

    public override string ToString() => Value;
}

public sealed class CapabilityOptions
{
    public bool Ai { get; init; }

    public bool Ocr { get; init; }

    public bool Replay { get; init; }

    public bool SemanticSearch { get; init; }

    public bool HybridSearch { get; init; }
}

public sealed class LawWatcherRuntimeOptions
{
    public string Profile { get; init; } = RuntimeProfile.DevLaptop.Value;

    public CapabilityOptions Capabilities { get; init; } = new()
    {
        Ai = true,
        Ocr = false,
        Replay = true,
        SemanticSearch = false,
        HybridSearch = false
    };
}

public sealed class WorkerLiteOptions
{
    public int MaxConcurrency { get; init; } = 1;

    public string[] EnabledPipelines { get; init; } =
    [
        "projection",
        "notifications"
    ];
}

public static class WorkerLitePipelineConfiguration
{
    private const string ConfigurationPath = "LawWatcher:WorkerLite:EnabledPipelines";
    private const string EnvironmentPrefix = "LawWatcher__WorkerLite__EnabledPipelines__";

    public static string[] ResolveEnabledPipelines(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var environmentOverride = ResolveEnvironmentOverride();
        if (environmentOverride.Length != 0)
        {
            return environmentOverride;
        }

        return configuration.GetSection(ConfigurationPath).Get<string[]>()
            ?? new WorkerLiteOptions().EnabledPipelines;
    }

    private static string[] ResolveEnvironmentOverride()
    {
        var valuesByIndex = new SortedDictionary<int, string>();

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key ||
                !key.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var indexText = key[EnvironmentPrefix.Length..];
            if (!int.TryParse(indexText, out var index))
            {
                continue;
            }

            if (entry.Value is string value && !string.IsNullOrWhiteSpace(value))
            {
                valuesByIndex[index] = value.Trim();
            }
        }

        return [.. valuesByIndex.Values];
    }
}

public sealed class LocalLlmWorkerOptions
{
    public string DefaultModel { get; init; } = "llama3.2:1b";

    public int MaxConcurrency { get; init; } = 1;

    public int UnloadAfterIdleSeconds { get; init; } = 120;
}

public sealed class OllamaOptions
{
    public string BaseUrl { get; init; } = "http://127.0.0.1:11434";

    public int RequestTimeoutSeconds { get; init; } = 300;
}

public sealed class LocalEmbeddingOptions
{
    public string DefaultModel { get; init; } = "nomic-embed-text";
}

public sealed class OpenSearchOptions
{
    public string BaseUrl { get; init; } = string.Empty;

    public string IndexName { get; init; } = "lawwatcher-search-documents";

    public int RequestTimeoutSeconds { get; init; } = 30;

    public int EmbeddingDimensions { get; init; } = 768;
}

public enum AiActivationMode
{
    Disabled = 0,
    OnDemand = 1,
    KeepWarm = 2,
    AlwaysHot = 3
}

public sealed record AiCapabilities(
    bool Enabled,
    AiActivationMode ActivationMode,
    int MaxConcurrency,
    TimeSpan UnloadAfterIdle);

public sealed record SearchCapabilities(
    bool UseSqlFullText,
    bool UseHybridSearch,
    bool UseSemanticSearch);

public sealed record SearchInfrastructureCapabilities(
    bool SupportsSqlFullText,
    bool SupportsHybridSearch);

public static class SearchCapabilitiesRuntimeResolver
{
    public static SearchCapabilities Resolve(
        SearchCapabilities configuredCapabilities,
        SearchInfrastructureCapabilities infrastructureCapabilities) =>
        configuredCapabilities with
        {
            UseSqlFullText = configuredCapabilities.UseSqlFullText && infrastructureCapabilities.SupportsSqlFullText,
            UseHybridSearch = configuredCapabilities.UseHybridSearch && infrastructureCapabilities.SupportsHybridSearch,
            UseSemanticSearch = configuredCapabilities.UseSemanticSearch && infrastructureCapabilities.SupportsHybridSearch
        };
}

public sealed record SystemCapabilities(
    RuntimeProfile RuntimeProfile,
    AiCapabilities Ai,
    SearchCapabilities Search,
    bool OcrEnabled,
    bool ReplayEnabled)
{
    public static SystemCapabilities FromOptions(RuntimeProfile runtimeProfile, CapabilityOptions options)
    {
        var aiCapabilities = runtimeProfile == RuntimeProfile.DevLaptop
            ? new AiCapabilities(
                options.Ai,
                options.Ai ? AiActivationMode.OnDemand : AiActivationMode.Disabled,
                options.Ai ? 1 : 0,
                options.Ai ? TimeSpan.FromMinutes(2) : TimeSpan.Zero)
            : new AiCapabilities(
                options.Ai,
                options.Ai ? AiActivationMode.KeepWarm : AiActivationMode.Disabled,
                options.Ai ? 2 : 0,
                options.Ai ? TimeSpan.FromMinutes(10) : TimeSpan.Zero);

        var searchCapabilities = runtimeProfile == RuntimeProfile.DevLaptop
            ? new SearchCapabilities(
                UseSqlFullText: true,
                UseHybridSearch: false,
                UseSemanticSearch: false)
            : new SearchCapabilities(
                UseSqlFullText: true,
                UseHybridSearch: options.HybridSearch,
                UseSemanticSearch: options.SemanticSearch);

        return new SystemCapabilities(
            runtimeProfile,
            aiCapabilities,
            searchCapabilities,
            OcrEnabled: options.Ocr,
            ReplayEnabled: options.Replay);
    }
}
