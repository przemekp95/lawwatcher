namespace LawWatcher.Notifications.Contracts;

public sealed record BillAlertResponse(
    Guid Id,
    Guid ProfileId,
    string ProfileName,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    DateOnly BillSubmittedOn,
    string AlertPolicy,
    IReadOnlyCollection<string> MatchedKeywords,
    DateTimeOffset CreatedAtUtc);
