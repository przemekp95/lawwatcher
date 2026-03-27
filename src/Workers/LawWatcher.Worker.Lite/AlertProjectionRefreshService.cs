using LawWatcher.LegislativeIntake.Application;
using LawWatcher.Notifications.Application;
using LawWatcher.TaxonomyAndProfiles.Application;

namespace LawWatcher.Worker.Lite;

public sealed record AlertProjectionRefreshResult(int GeneratedCount);

public sealed class AlertProjectionRefreshService(
    BillsQueryService billsQueryService,
    MonitoringProfilesQueryService profilesQueryService,
    AlertsQueryService alertsQueryService,
    AlertGenerationService alertGenerationService)
{
    public async Task<AlertProjectionRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        var bills = await billsQueryService.GetBillsAsync(cancellationToken);
        var profiles = await profilesQueryService.GetProfilesAsync(cancellationToken);
        var existingAlerts = await alertsQueryService.GetAlertsAsync(cancellationToken);

        await alertGenerationService.GenerateAlertsAsync(
            bills,
            profiles,
            DateTimeOffset.UtcNow,
            cancellationToken);

        var updatedAlerts = await alertsQueryService.GetAlertsAsync(cancellationToken);
        return new AlertProjectionRefreshResult(updatedAlerts.Count - existingAlerts.Count);
    }
}
