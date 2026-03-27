namespace LawWatcher.IntegrationApi.Contracts;

public sealed record CreateBackfillRequest(
    string Source,
    string Scope,
    DateOnly RequestedFrom,
    DateOnly? RequestedTo);
