using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Domain.Backfills;
using Microsoft.Extensions.Options;

namespace LawWatcher.Api.Runtime;

public sealed class BackfillRequestsBootstrapHostedService(
    IOptions<BootstrapOptions> options,
    BackfillRequestsQueryService queryService,
    BackfillRequestsCommandService commandService) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.EnableDemoData)
        {
            return;
        }

        var existingBackfills = await queryService.GetBackfillsAsync(cancellationToken);
        if (existingBackfills.Count != 0)
        {
            return;
        }

        var sejmBackfillId = Guid.Parse("FAAB3924-3E03-4AEB-9087-AE5692D2A4A5");
        await commandService.RequestAsync(new RequestBackfillCommand(
            sejmBackfillId,
            BackfillSource.Of("sejm"),
            BackfillScope.Of("current-term"),
            new DateOnly(2026, 01, 01),
            new DateOnly(2026, 03, 31),
            "admin"), cancellationToken);
        await commandService.MarkStartedAsync(new MarkBackfillStartedCommand(sejmBackfillId), cancellationToken);
        await commandService.MarkCompletedAsync(new MarkBackfillCompletedCommand(sejmBackfillId), cancellationToken);

        await commandService.RequestAsync(new RequestBackfillCommand(
            Guid.Parse("4FCA0C02-6D9F-4C77-B1B3-904A30B6A79C"),
            BackfillSource.Of("eli"),
            BackfillScope.Of("acts-2026"),
            new DateOnly(2026, 01, 01),
            null,
            "system"), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
