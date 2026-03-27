namespace LawWatcher.IntegrationApi.Contracts;

public sealed record BackfillRequestResponse(
    Guid Id,
    string Source,
    string Scope,
    string Status,
    string RequestedBy,
    DateOnly RequestedFrom,
    DateOnly? RequestedTo,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);
