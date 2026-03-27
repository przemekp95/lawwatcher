namespace LawWatcher.TaxonomyAndProfiles.Contracts;

public sealed record ChangeProfileSubscriptionAlertPolicyRequest(
    string AlertPolicy,
    int? DigestIntervalMinutes);
