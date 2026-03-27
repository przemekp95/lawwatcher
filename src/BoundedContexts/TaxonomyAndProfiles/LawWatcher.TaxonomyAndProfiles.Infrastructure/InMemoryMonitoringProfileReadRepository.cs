using LawWatcher.TaxonomyAndProfiles.Application;

namespace LawWatcher.TaxonomyAndProfiles.Infrastructure;

public sealed class InMemoryMonitoringProfileReadRepository : IMonitoringProfileReadRepository
{
    private static readonly MonitoringProfileReadModel[] Profiles =
    [
        new(
            Guid.Parse("B10E6346-14A2-4B25-9CE2-DF04AAB11E65"),
            "Podatki CIT",
            "immediate",
            ["CIT", "estoński CIT"]),
        new(
            Guid.Parse("C50A38A6-8355-421F-B25E-A5D803A76EAF"),
            "VAT i JPK",
            "digest",
            ["VAT", "JPK_V7"])
    ];

    public Task<IReadOnlyCollection<MonitoringProfileReadModel>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyCollection<MonitoringProfileReadModel>>(Profiles);
    }
}
