using LawWatcher.BuildingBlocks.Configuration;
using LawWatcher.LegislativeIntake.Application;
using LawWatcher.LegislativeProcess.Application;
using LawWatcher.LegislativeProcess.Domain.Processes;
using Microsoft.Extensions.Options;

namespace LawWatcher.Api.Runtime;

public sealed class LegislativeProcessBootstrapHostedService(
    IOptions<BootstrapOptions> options,
    BillsQueryService billsQueryService,
    ProcessesQueryService processesQueryService,
    LegislativeProcessCommandService commandService) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.EnableDemoData)
        {
            return;
        }

        var existingProcesses = await processesQueryService.GetProcessesAsync(cancellationToken);
        if (existingProcesses.Count != 0)
        {
            return;
        }

        var bills = await billsQueryService.GetBillsAsync(cancellationToken);
        var citBill = bills.Single(bill => bill.ExternalId == "X-310");
        var vatBill = bills.Single(bill => bill.ExternalId == "X-311");

        await commandService.StartAsync(new StartLegislativeProcessCommand(
            Guid.Parse("E5F57C3A-16EA-4DDB-81E3-0D3E8FF2EEC0"),
            citBill.Id,
            citBill.Title,
            citBill.ExternalId,
            LegislativeStage.Submitted(new DateOnly(2026, 03, 24))), cancellationToken);
        await commandService.RecordStageAsync(new RecordLegislativeStageCommand(
            Guid.Parse("E5F57C3A-16EA-4DDB-81E3-0D3E8FF2EEC0"),
            LegislativeStage.Of("first-reading", "First reading", new DateOnly(2026, 03, 26))), cancellationToken);

        await commandService.StartAsync(new StartLegislativeProcessCommand(
            Guid.Parse("9A9B7A12-B9C9-4B47-98C8-B627EB7E21D7"),
            vatBill.Id,
            vatBill.Title,
            vatBill.ExternalId,
            LegislativeStage.Submitted(new DateOnly(2026, 03, 25))), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
