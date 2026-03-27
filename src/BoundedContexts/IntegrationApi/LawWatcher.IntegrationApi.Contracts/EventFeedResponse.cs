namespace LawWatcher.IntegrationApi.Contracts;

public sealed record EventFeedResponse(
    string Id,
    string Type,
    string SubjectType,
    string SubjectId,
    string Title,
    string Summary,
    DateTimeOffset OccurredAtUtc);
