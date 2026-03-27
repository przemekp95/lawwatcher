using LawWatcher.BuildingBlocks.Domain;

namespace LawWatcher.AiEnrichment.Domain.Tasks;

public sealed record AiEnrichmentTaskId : ValueObject
{
    public AiEnrichmentTaskId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "AI enrichment task identifier cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public sealed record AiTaskKind : ValueObject
{
    private AiTaskKind(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static AiTaskKind Of(string value)
    {
        var normalized = NormalizeRequired(value, nameof(value), "AI task kind");
        return new AiTaskKind(normalized);
    }

    public override string ToString() => Value;

    private static string NormalizeRequired(string value, string paramName, string label)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"{label} cannot be empty.", paramName);
        }

        return normalized;
    }
}

public sealed record AiTaskSubject : ValueObject
{
    private AiTaskSubject(string type, Guid id, string title)
    {
        Type = type;
        Id = id;
        Title = title;
    }

    public string Type { get; }

    public Guid Id { get; }

    public string Title { get; }

    public static AiTaskSubject Create(string type, Guid id, string title)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "AI task subject identifier cannot be empty.");
        }

        return new AiTaskSubject(
            NormalizeRequired(type, nameof(type), "AI task subject type"),
            id,
            NormalizeRequired(title, nameof(title), "AI task subject title"));
    }

    private static string NormalizeRequired(string value, string paramName, string label)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"{label} cannot be empty.", paramName);
        }

        return normalized;
    }
}

public sealed record AiTaskStatus : ValueObject
{
    private AiTaskStatus(string code)
    {
        Code = code;
    }

    public string Code { get; }

    public static AiTaskStatus Queued() => new("queued");

    public static AiTaskStatus Running() => new("running");

    public static AiTaskStatus Completed() => new("completed");

    public static AiTaskStatus Failed() => new("failed");
}

public sealed record AiEnrichmentRequested(
    AiEnrichmentTaskId TaskId,
    string Kind,
    string SubjectType,
    Guid SubjectId,
    string SubjectTitle,
    string Prompt,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record AiEnrichmentProcessingStarted(
    AiEnrichmentTaskId TaskId,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record AiEnrichmentCompleted(
    AiEnrichmentTaskId TaskId,
    string Model,
    string Content,
    IReadOnlyCollection<string> Citations,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record AiEnrichmentFailed(
    AiEnrichmentTaskId TaskId,
    string Error,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed class AiEnrichmentTask : AggregateRoot<AiEnrichmentTaskId>
{
    private readonly List<string> _citations = [];

    private AiTaskKind _kind = AiTaskKind.Of("placeholder");
    private AiTaskSubject _subject = AiTaskSubject.Create("placeholder", Guid.Parse("11111111-1111-1111-1111-111111111111"), "placeholder");
    private AiTaskStatus _status = AiTaskStatus.Queued();
    private string _prompt = "placeholder";
    private string? _model;
    private string? _content;
    private string? _error;

    private AiEnrichmentTask()
    {
    }

    public AiTaskKind Kind => _kind;

    public AiTaskSubject Subject => _subject;

    public AiTaskStatus Status => _status;

    public string Prompt => _prompt;

    public string? Model => _model;

    public string? Content => _content;

    public string? Error => _error;

    public IReadOnlyCollection<string> Citations => _citations.AsReadOnly();

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public DateTimeOffset? FailedAtUtc { get; private set; }

    public static AiEnrichmentTask Request(
        AiEnrichmentTaskId id,
        AiTaskKind kind,
        AiTaskSubject subject,
        string prompt,
        DateTimeOffset occurredAtUtc)
    {
        var task = new AiEnrichmentTask();
        task.Raise(new AiEnrichmentRequested(
            id,
            kind.Value,
            subject.Type,
            subject.Id,
            subject.Title,
            NormalizePrompt(prompt),
            occurredAtUtc));
        return task;
    }

    public static AiEnrichmentTask Rehydrate(IEnumerable<IDomainEvent> history)
    {
        var task = new AiEnrichmentTask();
        task.LoadFromHistory(history);
        return task;
    }

    public void MarkStarted(DateTimeOffset occurredAtUtc)
    {
        if (_status.Code is "running" or "completed")
        {
            return;
        }

        Raise(new AiEnrichmentProcessingStarted(Id, occurredAtUtc));
    }

    public void MarkCompleted(
        string model,
        string content,
        IReadOnlyCollection<string> citations,
        DateTimeOffset occurredAtUtc)
    {
        if (_status.Code == "completed")
        {
            return;
        }

        Raise(new AiEnrichmentCompleted(
            Id,
            NormalizeRequired(model, nameof(model), "AI model"),
            NormalizeRequired(content, nameof(content), "AI content"),
            NormalizeCitations(citations),
            occurredAtUtc));
    }

    public void MarkFailed(string error, DateTimeOffset occurredAtUtc)
    {
        if (_status.Code is "completed" or "failed")
        {
            return;
        }

        Raise(new AiEnrichmentFailed(
            Id,
            NormalizeRequired(error, nameof(error), "AI failure reason"),
            occurredAtUtc));
    }

    protected override void Apply(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case AiEnrichmentRequested requested:
                Id = requested.TaskId;
                _kind = AiTaskKind.Of(requested.Kind);
                _subject = AiTaskSubject.Create(requested.SubjectType, requested.SubjectId, requested.SubjectTitle);
                _status = AiTaskStatus.Queued();
                _prompt = requested.Prompt;
                RequestedAtUtc = requested.OccurredAtUtc;
                _model = null;
                _content = null;
                _error = null;
                _citations.Clear();
                StartedAtUtc = null;
                CompletedAtUtc = null;
                FailedAtUtc = null;
                break;
            case AiEnrichmentProcessingStarted started:
                _status = AiTaskStatus.Running();
                StartedAtUtc = started.OccurredAtUtc;
                FailedAtUtc = null;
                break;
            case AiEnrichmentCompleted completed:
                _status = AiTaskStatus.Completed();
                _model = completed.Model;
                _content = completed.Content;
                _error = null;
                _citations.Clear();
                _citations.AddRange(NormalizeCitations(completed.Citations));
                StartedAtUtc ??= completed.OccurredAtUtc;
                CompletedAtUtc = completed.OccurredAtUtc;
                FailedAtUtc = null;
                break;
            case AiEnrichmentFailed failed:
                _status = AiTaskStatus.Failed();
                _error = failed.Error;
                StartedAtUtc ??= failed.OccurredAtUtc;
                FailedAtUtc = failed.OccurredAtUtc;
                CompletedAtUtc = null;
                break;
            default:
                throw new InvalidOperationException($"Unsupported domain event type '{domainEvent.GetType().Name}' for AI enrichment task.");
        }
    }

    private static string NormalizePrompt(string prompt) =>
        NormalizeRequired(prompt, nameof(prompt), "AI prompt");

    private static string NormalizeRequired(string value, string paramName, string label)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"{label} cannot be empty.", paramName);
        }

        return normalized;
    }

    private static IReadOnlyCollection<string> NormalizeCitations(IReadOnlyCollection<string> citations)
    {
        return citations
            .Select(citation => citation.Trim())
            .Where(citation => citation.Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
