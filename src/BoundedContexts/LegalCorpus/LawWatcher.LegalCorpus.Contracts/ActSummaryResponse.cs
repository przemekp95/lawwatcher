namespace LawWatcher.LegalCorpus.Contracts;

public sealed record ActSummaryResponse(
    Guid Id,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    string Eli,
    string Title,
    DateOnly PublishedOn,
    DateOnly? EffectiveFrom,
    IReadOnlyCollection<string> ArtifactKinds);
