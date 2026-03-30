using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.TaxonomyAndProfiles.Application;
using LawWatcher.TaxonomyAndProfiles.Domain.MonitoringProfiles;
using Microsoft.Extensions.Options;

namespace LawWatcher.Api.Runtime;

public sealed class MonitoringProfilesBootstrapHostedService(
    IOptions<BootstrapOptions> options,
    MonitoringProfilesQueryService queryService,
    MonitoringProfilesCommandService commandService) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.EnableDemoData)
        {
            return;
        }

        var existingProfiles = await queryService.GetProfilesAsync(cancellationToken);
        if (existingProfiles.Count != 0)
        {
            return;
        }

        var citProfileId = Guid.Parse("B10E6346-14A2-4B25-9CE2-DF04AAB11E65");
        await commandService.CreateAsync(new CreateMonitoringProfileCommand(
            citProfileId,
            "Podatki CIT",
            AlertPolicy.Immediate()), cancellationToken);
        await commandService.AddRuleAsync(new AddMonitoringProfileRuleCommand(
            citProfileId,
            ProfileRule.Keyword("CIT")), cancellationToken);
        await commandService.AddRuleAsync(new AddMonitoringProfileRuleCommand(
            citProfileId,
            ProfileRule.Keyword("estoński CIT")), cancellationToken);

        var vatProfileId = Guid.Parse("C50A38A6-8355-421F-B25E-A5D803A76EAF");
        await commandService.CreateAsync(new CreateMonitoringProfileCommand(
            vatProfileId,
            "VAT i JPK",
            AlertPolicy.Digest(TimeSpan.FromHours(12))), cancellationToken);
        await commandService.AddRuleAsync(new AddMonitoringProfileRuleCommand(
            vatProfileId,
            ProfileRule.Keyword("VAT")), cancellationToken);
        await commandService.AddRuleAsync(new AddMonitoringProfileRuleCommand(
            vatProfileId,
            ProfileRule.Keyword("JPK_V7")), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
