using LawWatcher.TaxonomyAndProfiles.Application;
using LawWatcher.TaxonomyAndProfiles.Domain.MonitoringProfiles;
using LawWatcher.TaxonomyAndProfiles.Domain.Subscriptions;
using LawWatcher.BuildingBlocks.Configuration;
using Microsoft.Extensions.Options;

namespace LawWatcher.Api.Runtime;

public sealed class ProfileSubscriptionsBootstrapHostedService(
    IOptionsMonitor<BootstrapOptions> bootstrapOptions,
    MonitoringProfilesQueryService profilesQueryService,
    ProfileSubscriptionsQueryService subscriptionsQueryService,
    ProfileSubscriptionsCommandService commandService) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!bootstrapOptions.CurrentValue.EnableDemoData)
        {
            return;
        }

        var existingSubscriptions = await subscriptionsQueryService.GetSubscriptionsAsync(cancellationToken);
        if (existingSubscriptions.Count != 0)
        {
            return;
        }

        var profiles = await profilesQueryService.GetProfilesAsync(cancellationToken);
        var citProfile = profiles.Single(profile => profile.Name == "Podatki CIT");
        var vatProfile = profiles.Single(profile => profile.Name == "VAT i JPK");

        await commandService.CreateAsync(new CreateProfileSubscriptionCommand(
            Guid.Parse("E0B5124C-B353-47FF-A89D-D77E6A696B55"),
            citProfile.Id,
            citProfile.Name,
            "anna.nowak@example.test",
            SubscriptionChannel.Email(),
            AlertPolicy.Immediate()), cancellationToken);
        await commandService.CreateAsync(new CreateProfileSubscriptionCommand(
            Guid.Parse("8C806C18-9926-4FCE-A539-B864C623CB52"),
            vatProfile.Id,
            vatProfile.Name,
            "marek.kowalski@example.test",
            SubscriptionChannel.Email(),
            AlertPolicy.Digest(TimeSpan.FromHours(12))), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
