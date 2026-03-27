using LawWatcher.BuildingBlocks.Domain;

namespace LawWatcher.LegislativeProcess.Domain.Processes;

public sealed record LegislativeProcessId : ValueObject
{
    public LegislativeProcessId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Legislative process identifier cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

public sealed record LinkedBillReference(Guid BillId, string BillTitle, string BillExternalId) : ValueObject
{
    public static LinkedBillReference Create(Guid billId, string billTitle, string billExternalId)
    {
        if (billId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(billId), "Linked bill identifier cannot be empty.");
        }

        return new LinkedBillReference(
            billId,
            NormalizeRequired(billTitle, nameof(billTitle)),
            NormalizeRequired(billExternalId, nameof(billExternalId)));
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Value cannot be empty.", paramName);
        }

        return normalized;
    }
}

public sealed record LegislativeStage(string Code, string Label, DateOnly OccurredOn) : ValueObject
{
    public static LegislativeStage Submitted(DateOnly occurredOn) => Of("submitted", "Submitted", occurredOn);

    public static LegislativeStage Of(string code, string label, DateOnly occurredOn)
    {
        return new LegislativeStage(
            NormalizeRequired(code, nameof(code)),
            NormalizeRequired(label, nameof(label)),
            occurredOn);
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Value cannot be empty.", paramName);
        }

        return normalized;
    }
}

public sealed record LegislativeProcessStarted(
    LegislativeProcessId ProcessId,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    string StageCode,
    string StageLabel,
    DateOnly StageOccurredOn,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed record LegislativeStageRecorded(
    LegislativeProcessId ProcessId,
    string StageCode,
    string StageLabel,
    DateOnly StageOccurredOn,
    DateTimeOffset OccurredAtUtc) : DomainEvent(Guid.NewGuid(), OccurredAtUtc);

public sealed class LegislativeProcess : AggregateRoot<LegislativeProcessId>
{
    private readonly List<LegislativeStage> _stages = [];

    private LinkedBillReference _bill = LinkedBillReference.Create(Guid.Parse("11111111-1111-1111-1111-111111111111"), "placeholder", "placeholder");
    private LegislativeStage _currentStage = LegislativeStage.Submitted(new DateOnly(2000, 01, 01));

    private LegislativeProcess()
    {
    }

    public LinkedBillReference Bill => _bill;

    public LegislativeStage CurrentStage => _currentStage;

    public IReadOnlyCollection<LegislativeStage> Stages => _stages.AsReadOnly();

    public static LegislativeProcess Start(
        LegislativeProcessId id,
        LinkedBillReference bill,
        LegislativeStage initialStage,
        DateTimeOffset occurredAtUtc)
    {
        var process = new LegislativeProcess();
        process.Raise(new LegislativeProcessStarted(
            id,
            bill.BillId,
            bill.BillTitle,
            bill.BillExternalId,
            initialStage.Code,
            initialStage.Label,
            initialStage.OccurredOn,
            occurredAtUtc));
        return process;
    }

    public static LegislativeProcess Rehydrate(IEnumerable<IDomainEvent> history)
    {
        var process = new LegislativeProcess();
        process.LoadFromHistory(history);
        return process;
    }

    public void RecordStage(LegislativeStage stage, DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(stage);

        if (_stages.Contains(stage))
        {
            return;
        }

        Raise(new LegislativeStageRecorded(Id, stage.Code, stage.Label, stage.OccurredOn, occurredAtUtc));
    }

    protected override void Apply(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case LegislativeProcessStarted started:
                Id = started.ProcessId;
                _bill = LinkedBillReference.Create(started.BillId, started.BillTitle, started.BillExternalId);
                _stages.Clear();
                _stages.Add(LegislativeStage.Of(started.StageCode, started.StageLabel, started.StageOccurredOn));
                RecalculateCurrentStage();
                break;
            case LegislativeStageRecorded recorded:
                _stages.Add(LegislativeStage.Of(recorded.StageCode, recorded.StageLabel, recorded.StageOccurredOn));
                RecalculateCurrentStage();
                break;
            default:
                throw new InvalidOperationException($"Unsupported domain event type '{domainEvent.GetType().Name}' for legislative process.");
        }
    }

    private void RecalculateCurrentStage()
    {
        _currentStage = _stages
            .OrderByDescending(stage => stage.OccurredOn)
            .ThenBy(stage => stage.Code, StringComparer.OrdinalIgnoreCase)
            .First();
    }
}
