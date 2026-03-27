using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.IntegrationApi.Contracts;
using LawWatcher.SearchAndDiscovery.Application;
using LawWatcher.SearchAndDiscovery.Contracts;

namespace LawWatcher.Api.Runtime;

public static class SystemCapabilitiesResponseFactory
{
    public static SystemCapabilitiesResponse Create(SystemCapabilities capabilities)
    {
        var backendSelector = new SearchBackendSelector();
        var backend = backendSelector.Select(capabilities.Search);

        return new SystemCapabilitiesResponse(
            capabilities.RuntimeProfile.Value,
            new AiCapabilityResponse(
                capabilities.Ai.Enabled,
                capabilities.Ai.ActivationMode.ToString(),
                capabilities.Ai.MaxConcurrency,
                (int)capabilities.Ai.UnloadAfterIdle.TotalSeconds),
            new SearchCapabilityResponse(
                capabilities.Search.UseSqlFullText,
                capabilities.Search.UseHybridSearch,
                capabilities.Search.UseSemanticSearch,
                backend),
            capabilities.OcrEnabled,
            capabilities.ReplayEnabled);
    }

    public static SearchQueryResponse CreateSearchResponse(SearchQueryResult result, SystemCapabilities capabilities)
    {
        var backendSelector = new SearchBackendSelector();
        var backend = backendSelector.Select(capabilities.Search);

        return new SearchQueryResponse(
            result.Query,
            backend,
            result.Hits
                .Select(hit => new SearchHitResponse(hit.Id, hit.Title, hit.Type, hit.Snippet))
                .ToArray());
    }
}
