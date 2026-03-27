using LawWatcher.TaxonomyAndProfiles.Contracts;

namespace LawWatcher.TaxonomyAndProfiles.Application;

public sealed record MonitoringProfileReadModel(
    Guid Id,
    string Name,
    string AlertPolicy,
    IReadOnlyCollection<string> Keywords);

public interface IMonitoringProfileReadRepository
{
    Task<IReadOnlyCollection<MonitoringProfileReadModel>> GetProfilesAsync(CancellationToken cancellationToken);
}

public sealed class MonitoringProfilesQueryService(IMonitoringProfileReadRepository repository)
{
    public async Task<IReadOnlyList<MonitoringProfileResponse>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        var profiles = await repository.GetProfilesAsync(cancellationToken);

        return profiles
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .Select(profile => new MonitoringProfileResponse(
                profile.Id,
                profile.Name,
                profile.AlertPolicy,
                profile.Keywords.ToArray(),
                profile.Keywords.Count))
            .ToArray();
    }
}
