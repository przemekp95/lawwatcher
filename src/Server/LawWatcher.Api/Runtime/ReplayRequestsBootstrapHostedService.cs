using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Domain.Replays;
using Microsoft.Extensions.Options;

namespace LawWatcher.Api.Runtime;

public sealed class ReplayRequestsBootstrapHostedService(
    IOptions<BootstrapOptions> options,
    ReplayRequestsQueryService queryService,
    ReplayRequestsCommandService commandService) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.EnableDemoData)
        {
            return;
        }

        var existingReplays = await queryService.GetReplaysAsync(cancellationToken);
        if (existingReplays.Count != 0)
        {
            return;
        }

        var searchReplayId = Guid.Parse("11F13623-2F8F-46CB-A397-A3D2FEA21F2F");
        await commandService.RequestAsync(new RequestReplayCommand(
            searchReplayId,
            ReplayScope.Of("search-index"),
            "system"), cancellationToken);
        await commandService.MarkStartedAsync(new MarkReplayStartedCommand(searchReplayId), cancellationToken);
        await commandService.MarkCompletedAsync(new MarkReplayCompletedCommand(searchReplayId), cancellationToken);

        await commandService.RequestAsync(new RequestReplayCommand(
            Guid.Parse("DFBE8ADB-E6F5-4130-8D3B-8D52A451EA27"),
            ReplayScope.Of("sql-projections"),
            "admin"), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
