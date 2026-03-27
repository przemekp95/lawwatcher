namespace LawWatcher.TaxonomyAndProfiles.Contracts;

public sealed record CreateProfileSubscriptionRequest(
    Guid ProfileId,
    string Subscriber,
    string Channel,
    string AlertPolicy,
    int? DigestIntervalMinutes);
