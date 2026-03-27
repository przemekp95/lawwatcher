namespace LawWatcher.IntegrationApi.Contracts;

public sealed record ReplayRequestResponse(
    Guid Id,
    string Scope,
    string Status,
    string RequestedBy,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);
