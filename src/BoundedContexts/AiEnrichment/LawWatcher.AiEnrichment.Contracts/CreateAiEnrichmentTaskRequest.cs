namespace LawWatcher.AiEnrichment.Contracts;

public sealed record CreateAiEnrichmentTaskRequest(
    string Kind,
    string SubjectType,
    Guid SubjectId,
    string SubjectTitle,
    string Prompt);
