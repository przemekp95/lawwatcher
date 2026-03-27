namespace LawWatcher.TaxonomyAndProfiles.Contracts;

public sealed record CreateMonitoringProfileRequest(
    string Name,
    string AlertPolicy,
    int? DigestIntervalMinutes,
    IReadOnlyCollection<string> Keywords);
