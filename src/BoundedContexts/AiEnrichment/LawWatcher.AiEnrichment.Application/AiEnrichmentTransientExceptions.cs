namespace LawWatcher.AiEnrichment.Application;

public sealed class DerivedDocumentTextNotReadyException : IOException
{
    public DerivedDocumentTextNotReadyException(string sourceObjectKey)
        : base($"Derived document text is not available yet for source artifact '{sourceObjectKey}'.")
    {
        SourceObjectKey = sourceObjectKey;
    }

    public string SourceObjectKey { get; }
}
