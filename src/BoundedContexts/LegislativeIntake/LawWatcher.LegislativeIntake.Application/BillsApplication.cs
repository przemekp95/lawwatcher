using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.LegislativeIntake.Contracts;
using LawWatcher.LegislativeIntake.Domain.Bills;
using System.Linq;

namespace LawWatcher.LegislativeIntake.Application;

public sealed record RegisterBillCommand(
    Guid BillId,
    string SourceSystem,
    string ExternalId,
    string SourceUrl,
    string Title,
    DateOnly SubmittedOn) : Command;

public sealed record AttachBillDocumentCommand(
    Guid BillId,
    string Kind,
    string ObjectKey) : Command;

public sealed record ImportedBillReadModel(
    Guid Id,
    string SourceSystem,
    string ExternalId,
    string Title,
    string SourceUrl,
    DateOnly SubmittedOn,
    IReadOnlyCollection<string> DocumentKinds);

public interface IImportedBillRepository
{
    Task<ImportedBill?> GetAsync(BillId id, CancellationToken cancellationToken);

    Task SaveAsync(ImportedBill bill, CancellationToken cancellationToken);
}

public interface IImportedBillOutboxWriter
{
    Task SaveAsync(
        ImportedBill bill,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken);
}

public interface IImportedBillReadRepository
{
    Task<IReadOnlyCollection<ImportedBillReadModel>> GetBillsAsync(CancellationToken cancellationToken);
}

public interface IImportedBillProjection
{
    Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}

public sealed class LegislativeIntakeCommandService(
    IImportedBillRepository repository,
    IImportedBillProjection projection)
{
    public async Task RegisterAsync(RegisterBillCommand command, CancellationToken cancellationToken)
    {
        var billId = new BillId(command.BillId);
        var existing = await repository.GetAsync(billId, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Bill '{command.BillId}' has already been imported.");
        }

        var bill = ImportedBill.Import(
            billId,
            ExternalBillReference.Create(command.SourceSystem, command.ExternalId, command.SourceUrl),
            command.Title,
            command.SubmittedOn,
            command.RequestedAtUtc);

        await SaveAndProjectAsync(bill, cancellationToken);
    }

    public async Task AttachDocumentAsync(AttachBillDocumentCommand command, CancellationToken cancellationToken)
    {
        var bill = await repository.GetAsync(new BillId(command.BillId), cancellationToken)
            ?? throw new InvalidOperationException($"Bill '{command.BillId}' was not found.");

        bill.AttachDocument(BillDocument.Create(command.Kind, command.ObjectKey), command.RequestedAtUtc);
        await SaveAndProjectAsync(bill, cancellationToken);
    }

    private async Task SaveAndProjectAsync(ImportedBill bill, CancellationToken cancellationToken)
    {
        var pendingEvents = bill.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var integrationEvents = new List<IIntegrationEvent>(pendingEvents.Length);
        foreach (var domainEvent in pendingEvents)
        {
            switch (domainEvent)
            {
                case BillImported imported:
                    integrationEvents.Add(new BillImportedIntegrationEvent(
                        imported.EventId,
                        imported.OccurredAtUtc,
                        imported.BillId.Value,
                        imported.SourceSystem,
                        imported.ExternalId,
                        imported.SourceUrl,
                        imported.Title,
                        imported.SubmittedOn));
                    break;
                case BillDocumentAttached attached:
                    integrationEvents.Add(new BillDocumentAttachedIntegrationEvent(
                        attached.EventId,
                        attached.OccurredAtUtc,
                        attached.BillId.Value,
                        attached.Kind,
                        attached.ObjectKey));
                    break;
            }
        }

        if (repository is IImportedBillOutboxWriter outboxWriter && integrationEvents.Count != 0)
        {
            await outboxWriter.SaveAsync(bill, integrationEvents, cancellationToken);
        }
        else
        {
            await repository.SaveAsync(bill, cancellationToken);
        }

        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }
}

public sealed class BillsQueryService(IImportedBillReadRepository repository)
{
    public async Task<IReadOnlyList<BillSummaryResponse>> GetBillsAsync(CancellationToken cancellationToken)
    {
        var bills = await repository.GetBillsAsync(cancellationToken);

        return bills
            .OrderByDescending(bill => bill.SubmittedOn)
            .ThenBy(bill => bill.Title, StringComparer.OrdinalIgnoreCase)
            .Select(bill => new BillSummaryResponse(
                bill.Id,
                bill.SourceSystem,
                bill.ExternalId,
                bill.Title,
                bill.SourceUrl,
                bill.SubmittedOn,
                bill.DocumentKinds.ToArray()))
            .ToArray();
    }
}
