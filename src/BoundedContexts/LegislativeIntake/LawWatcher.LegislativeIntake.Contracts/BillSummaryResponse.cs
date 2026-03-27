namespace LawWatcher.LegislativeIntake.Contracts;

public sealed record BillSummaryResponse(
    Guid Id,
    string SourceSystem,
    string ExternalId,
    string Title,
    string SourceUrl,
    DateOnly SubmittedOn,
    IReadOnlyCollection<string> DocumentKinds);
