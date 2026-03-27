namespace LawWatcher.SearchAndDiscovery.Contracts;

public enum SearchBackend
{
    SqlFullText = 0,
    HybridVector = 1,
    ProjectionIndex = 2
}
