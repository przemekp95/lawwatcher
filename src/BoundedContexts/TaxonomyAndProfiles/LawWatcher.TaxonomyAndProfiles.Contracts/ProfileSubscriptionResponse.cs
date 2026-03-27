namespace LawWatcher.TaxonomyAndProfiles.Contracts;

public sealed record ProfileSubscriptionResponse(
    Guid Id,
    Guid ProfileId,
    string ProfileName,
    string Subscriber,
    string Channel,
    string AlertPolicy,
    int? DigestIntervalMinutes);
