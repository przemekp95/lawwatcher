using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.LegalCorpus.Contracts;
using LawWatcher.LegalCorpus.Domain.Acts;

namespace LawWatcher.LegalCorpus.Application;

public sealed record RegisterActCommand(
    Guid ActId,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    string Eli,
    string Title,
    DateOnly PublishedOn,
    DateOnly? EffectiveFrom) : Command;

public sealed record AttachActArtifactCommand(
    Guid ActId,
    string Kind,
    string ObjectKey) : Command;

public sealed record PublishedActReadModel(
    Guid Id,
    Guid BillId,
    string BillTitle,
    string BillExternalId,
    string Eli,
    string Title,
    DateOnly PublishedOn,
    DateOnly? EffectiveFrom,
    IReadOnlyCollection<string> ArtifactKinds);

public interface IPublishedActRepository
{
    Task<PublishedAct?> GetAsync(ActId id, CancellationToken cancellationToken);

    Task SaveAsync(PublishedAct act, CancellationToken cancellationToken);
}

public interface IPublishedActOutboxWriter
{
    Task SaveAsync(
        PublishedAct act,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken);
}

public interface IPublishedActReadRepository
{
    Task<IReadOnlyCollection<PublishedActReadModel>> GetActsAsync(CancellationToken cancellationToken);
}

public interface IPublishedActProjection
{
    Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}

public sealed class LegalCorpusCommandService(
    IPublishedActRepository repository,
    IPublishedActProjection projection)
{
    public async Task RegisterAsync(RegisterActCommand command, CancellationToken cancellationToken)
    {
        var actId = new ActId(command.ActId);
        var existing = await repository.GetAsync(actId, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Published act '{command.ActId}' has already been registered.");
        }

        var act = PublishedAct.Register(
            actId,
            OriginatingBillReference.Create(command.BillId, command.BillTitle, command.BillExternalId),
            EliReference.Create(command.Eli),
            command.Title,
            command.PublishedOn,
            command.EffectiveFrom,
            command.RequestedAtUtc);

        await SaveAndProjectAsync(act, cancellationToken);
    }

    public async Task AttachArtifactAsync(AttachActArtifactCommand command, CancellationToken cancellationToken)
    {
        var act = await repository.GetAsync(new ActId(command.ActId), cancellationToken)
            ?? throw new InvalidOperationException($"Published act '{command.ActId}' was not found.");

        act.AttachArtifact(ActArtifact.Create(command.Kind, command.ObjectKey), command.RequestedAtUtc);
        await SaveAndProjectAsync(act, cancellationToken);
    }

    private async Task SaveAndProjectAsync(PublishedAct act, CancellationToken cancellationToken)
    {
        var pendingEvents = act.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var integrationEvents = new List<IIntegrationEvent>(pendingEvents.Length);
        foreach (var domainEvent in pendingEvents)
        {
            switch (domainEvent)
            {
                case PublishedActRegistered registered:
                    integrationEvents.Add(new PublishedActRegisteredIntegrationEvent(
                        registered.EventId,
                        registered.OccurredAtUtc,
                        registered.ActId.Value,
                        registered.BillId,
                        registered.BillTitle,
                        registered.BillExternalId,
                        registered.Eli,
                        registered.Title,
                        registered.PublishedOn,
                        registered.EffectiveFrom));
                    break;
                case ActArtifactAttached attached:
                    integrationEvents.Add(new ActArtifactAttachedIntegrationEvent(
                        attached.EventId,
                        attached.OccurredAtUtc,
                        attached.ActId.Value,
                        attached.Kind,
                        attached.ObjectKey));
                    break;
            }
        }

        if (repository is IPublishedActOutboxWriter outboxWriter && integrationEvents.Count != 0)
        {
            await outboxWriter.SaveAsync(act, integrationEvents, cancellationToken);
        }
        else
        {
            await repository.SaveAsync(act, cancellationToken);
        }

        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }
}

public sealed class ActsQueryService(IPublishedActReadRepository repository)
{
    public async Task<IReadOnlyList<ActSummaryResponse>> GetActsAsync(CancellationToken cancellationToken)
    {
        var acts = await repository.GetActsAsync(cancellationToken);

        return acts
            .OrderByDescending(act => act.PublishedOn)
            .ThenBy(act => act.Title, StringComparer.OrdinalIgnoreCase)
            .Select(act => new ActSummaryResponse(
                act.Id,
                act.BillId,
                act.BillTitle,
                act.BillExternalId,
                act.Eli,
                act.Title,
                act.PublishedOn,
                act.EffectiveFrom,
                act.ArtifactKinds.ToArray()))
            .ToArray();
    }
}
