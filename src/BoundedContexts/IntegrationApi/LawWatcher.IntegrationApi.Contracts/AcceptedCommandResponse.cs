namespace LawWatcher.IntegrationApi.Contracts;

public sealed record AcceptedCommandResponse(
    Guid Id,
    string Status);
