namespace LawWatcher.AiEnrichment.Contracts;

public sealed record AiEnrichmentTaskResponse(
    Guid Id,
    string Kind,
    string SubjectType,
    Guid SubjectId,
    string SubjectTitle,
    string Status,
    string? Model,
    string? Content,
    string? Error,
    IReadOnlyCollection<string> Citations,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? FailedAtUtc);
