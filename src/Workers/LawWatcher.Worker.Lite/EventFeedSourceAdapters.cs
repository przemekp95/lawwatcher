using LawWatcher.IntegrationApi.Application;
using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegislativeIntake.Application;
using LawWatcher.LegislativeProcess.Application;
using LawWatcher.Notifications.Application;

namespace LawWatcher.Worker.Lite;

public sealed class BillsEventFeedSource(BillsQueryService queryService) : IEventFeedSource
{
    public async Task<IReadOnlyCollection<EventFeedItem>> GetEventsAsync(CancellationToken cancellationToken)
    {
        var bills = await queryService.GetBillsAsync(cancellationToken);

        return bills
            .Select(bill => new EventFeedItem(
                $"bill:{bill.Id:D}",
                "bill.imported",
                "bill",
                bill.Id.ToString("D"),
                bill.Title,
                $"Imported from {bill.SourceSystem} as {bill.ExternalId}.",
                EventFeedTime.AsUtc(bill.SubmittedOn, 8)))
            .ToArray();
    }
}

public sealed class ProcessesEventFeedSource(ProcessesQueryService queryService) : IEventFeedSource
{
    public async Task<IReadOnlyCollection<EventFeedItem>> GetEventsAsync(CancellationToken cancellationToken)
    {
        var processes = await queryService.GetProcessesAsync(cancellationToken);

        return processes
            .Select(process => new EventFeedItem(
                $"process:{process.Id:D}",
                "process.updated",
                "process",
                process.Id.ToString("D"),
                process.BillTitle,
                $"Current stage: {process.CurrentStageLabel} ({process.CurrentStageCode}).",
                EventFeedTime.AsUtc(process.LastUpdatedOn, 12)))
            .ToArray();
    }
}

public sealed class ActsEventFeedSource(ActsQueryService queryService) : IEventFeedSource
{
    public async Task<IReadOnlyCollection<EventFeedItem>> GetEventsAsync(CancellationToken cancellationToken)
    {
        var acts = await queryService.GetActsAsync(cancellationToken);

        return acts
            .Select(act => new EventFeedItem(
                $"act:{act.Id:D}",
                "act.published",
                "act",
                act.Id.ToString("D"),
                act.Title,
                $"ELI: {act.Eli}.",
                EventFeedTime.AsUtc(act.PublishedOn, 16)))
            .ToArray();
    }
}

public sealed class AlertsEventFeedSource(AlertsQueryService queryService) : IEventFeedSource
{
    public async Task<IReadOnlyCollection<EventFeedItem>> GetEventsAsync(CancellationToken cancellationToken)
    {
        var alerts = await queryService.GetAlertsAsync(cancellationToken);

        return alerts
            .Select(alert => new EventFeedItem(
                $"alert:{alert.Id:D}",
                "alert.created",
                "alert",
                alert.Id.ToString("D"),
                alert.BillTitle,
                $"Profile {alert.ProfileName}.",
                alert.CreatedAtUtc))
            .ToArray();
    }
}

public sealed class ReplaysEventFeedSource(ReplayRequestsQueryService queryService) : IEventFeedSource
{
    public async Task<IReadOnlyCollection<EventFeedItem>> GetEventsAsync(CancellationToken cancellationToken)
    {
        var replays = await queryService.GetReplaysAsync(cancellationToken);

        return replays
            .Select(replay => new EventFeedItem(
                $"replay:{replay.Id:D}",
                $"replay.{replay.Status}",
                "replay",
                replay.Id.ToString("D"),
                replay.Scope,
                $"Requested by {replay.RequestedBy}.",
                replay.CompletedAtUtc ?? replay.StartedAtUtc ?? replay.RequestedAtUtc))
            .ToArray();
    }
}

public sealed class BackfillsEventFeedSource(BackfillRequestsQueryService queryService) : IEventFeedSource
{
    public async Task<IReadOnlyCollection<EventFeedItem>> GetEventsAsync(CancellationToken cancellationToken)
    {
        var backfills = await queryService.GetBackfillsAsync(cancellationToken);

        return backfills
            .Select(backfill => new EventFeedItem(
                $"backfill:{backfill.Id:D}",
                $"backfill.{backfill.Status}",
                "backfill",
                backfill.Id.ToString("D"),
                $"{backfill.Source}:{backfill.Scope}",
                $"Requested by {backfill.RequestedBy}.",
                backfill.CompletedAtUtc ?? backfill.StartedAtUtc ?? backfill.RequestedAtUtc))
            .ToArray();
    }
}

internal static class EventFeedTime
{
    public static DateTimeOffset AsUtc(DateOnly date, int hour)
    {
        return new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero);
    }
}
