using LawWatcher.BuildingBlocks.Domain;
using LawWatcher.BuildingBlocks.Messaging;
using LawWatcher.TaxonomyAndProfiles.Contracts;
using LawWatcher.TaxonomyAndProfiles.Domain.MonitoringProfiles;

namespace LawWatcher.TaxonomyAndProfiles.Application;

public sealed record CreateMonitoringProfileCommand(
    Guid ProfileId,
    string Name,
    AlertPolicy AlertPolicy) : Command;

public sealed record AddMonitoringProfileRuleCommand(
    Guid ProfileId,
    ProfileRule Rule) : Command;

public sealed record ChangeMonitoringProfileAlertPolicyCommand(
    Guid ProfileId,
    AlertPolicy AlertPolicy) : Command;

public sealed record DeactivateMonitoringProfileCommand(Guid ProfileId) : Command;

public interface IMonitoringProfileRepository
{
    Task<MonitoringProfile?> GetAsync(MonitoringProfileId id, CancellationToken cancellationToken);

    Task SaveAsync(MonitoringProfile profile, CancellationToken cancellationToken);
}

public interface IMonitoringProfileOutboxWriter
{
    Task SaveAsync(
        MonitoringProfile profile,
        IReadOnlyCollection<IIntegrationEvent> integrationEvents,
        CancellationToken cancellationToken);
}

public interface IMonitoringProfileProjection
{
    Task ProjectAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}

public sealed class MonitoringProfilesCommandService(
    IMonitoringProfileRepository repository,
    IMonitoringProfileProjection projection)
{
    public async Task CreateAsync(CreateMonitoringProfileCommand command, CancellationToken cancellationToken)
    {
        var profileId = new MonitoringProfileId(command.ProfileId);
        var existing = await repository.GetAsync(profileId, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Monitoring profile '{command.ProfileId}' already exists.");
        }

        var profile = MonitoringProfile.Create(
            profileId,
            command.Name,
            command.AlertPolicy,
            command.RequestedAtUtc);

        await SaveAndProjectAsync(profile, cancellationToken);
    }

    public async Task AddRuleAsync(AddMonitoringProfileRuleCommand command, CancellationToken cancellationToken)
    {
        var profile = await repository.GetAsync(new MonitoringProfileId(command.ProfileId), cancellationToken)
            ?? throw new InvalidOperationException($"Monitoring profile '{command.ProfileId}' was not found.");

        profile.AddRule(command.Rule, command.RequestedAtUtc);
        await SaveAndProjectAsync(profile, cancellationToken);
    }

    public async Task ChangeAlertPolicyAsync(ChangeMonitoringProfileAlertPolicyCommand command, CancellationToken cancellationToken)
    {
        var profile = await repository.GetAsync(new MonitoringProfileId(command.ProfileId), cancellationToken)
            ?? throw new InvalidOperationException($"Monitoring profile '{command.ProfileId}' was not found.");

        profile.ChangeAlertPolicy(command.AlertPolicy, command.RequestedAtUtc);
        await SaveAndProjectAsync(profile, cancellationToken);
    }

    public async Task DeactivateAsync(DeactivateMonitoringProfileCommand command, CancellationToken cancellationToken)
    {
        var profile = await repository.GetAsync(new MonitoringProfileId(command.ProfileId), cancellationToken)
            ?? throw new InvalidOperationException($"Monitoring profile '{command.ProfileId}' was not found.");

        profile.Deactivate(command.RequestedAtUtc);
        await SaveAndProjectAsync(profile, cancellationToken);
    }

    private async Task SaveAndProjectAsync(MonitoringProfile profile, CancellationToken cancellationToken)
    {
        var pendingEvents = profile.UncommittedEvents.ToArray();
        if (pendingEvents.Length == 0)
        {
            return;
        }

        var integrationEvents = new List<IIntegrationEvent>(pendingEvents.Length);
        foreach (var domainEvent in pendingEvents)
        {
            switch (domainEvent)
            {
                case MonitoringProfileCreated created:
                    integrationEvents.Add(new MonitoringProfileCreatedIntegrationEvent(
                        created.EventId,
                        created.OccurredAtUtc,
                        created.ProfileId.Value,
                        created.Name,
                        created.AlertPolicyCode,
                        created.DigestInterval));
                    break;
                case MonitoringProfileRuleAdded added:
                    integrationEvents.Add(new MonitoringProfileRuleAddedIntegrationEvent(
                        added.EventId,
                        added.OccurredAtUtc,
                        added.ProfileId.Value,
                        added.RuleKind,
                        added.RuleValue));
                    break;
                case MonitoringProfileAlertPolicyChanged changed:
                    integrationEvents.Add(new MonitoringProfileAlertPolicyChangedIntegrationEvent(
                        changed.EventId,
                        changed.OccurredAtUtc,
                        changed.ProfileId.Value,
                        changed.AlertPolicyCode,
                        changed.DigestInterval));
                    break;
                case MonitoringProfileDeactivated deactivated:
                    integrationEvents.Add(new MonitoringProfileDeactivatedIntegrationEvent(
                        deactivated.EventId,
                        deactivated.OccurredAtUtc,
                        deactivated.ProfileId.Value));
                    break;
            }
        }

        if (repository is IMonitoringProfileOutboxWriter outboxWriter && integrationEvents.Count != 0)
        {
            await outboxWriter.SaveAsync(profile, integrationEvents, cancellationToken);
        }
        else
        {
            await repository.SaveAsync(profile, cancellationToken);
        }

        await projection.ProjectAsync(pendingEvents, cancellationToken);
    }
}
