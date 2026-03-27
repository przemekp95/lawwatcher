namespace LawWatcher.TaxonomyAndProfiles.Contracts;

public sealed record MonitoringProfileResponse(
    Guid Id,
    string Name,
    string AlertPolicy,
    IReadOnlyCollection<string> Keywords,
    int RuleCount);
