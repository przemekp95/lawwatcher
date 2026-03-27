using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.SearchAndDiscovery.Contracts;

namespace LawWatcher.SearchAndDiscovery.Application;

public sealed class SearchBackendSelector
{
    public SearchBackend Select(SearchCapabilities capabilities) =>
        capabilities.UseHybridSearch
            ? SearchBackend.HybridVector
            : capabilities.UseSqlFullText
                ? SearchBackend.SqlFullText
                : SearchBackend.ProjectionIndex;
}
