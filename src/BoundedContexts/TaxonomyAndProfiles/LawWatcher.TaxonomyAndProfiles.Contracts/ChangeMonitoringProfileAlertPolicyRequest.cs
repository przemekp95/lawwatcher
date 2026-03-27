namespace LawWatcher.TaxonomyAndProfiles.Contracts;

public sealed record ChangeMonitoringProfileAlertPolicyRequest(
    string AlertPolicy,
    int? DigestIntervalMinutes);
