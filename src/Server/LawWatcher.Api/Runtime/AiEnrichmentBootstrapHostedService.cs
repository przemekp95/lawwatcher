using LawWatcher.AiEnrichment.Application;
using LawWatcher.LegislativeIntake.Application;
using LawWatcher.LegalCorpus.Application;

namespace LawWatcher.Api.Runtime;

public sealed class AiEnrichmentBootstrapHostedService(
    ISystemCapabilitiesProvider capabilitiesProvider,
    AiEnrichmentTasksQueryService tasksQueryService,
    AiEnrichmentCommandService commandService,
    BillsQueryService billsQueryService,
    ActsQueryService actsQueryService) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!capabilitiesProvider.Current.Ai.Enabled)
        {
            return;
        }

        var existingTasks = await tasksQueryService.GetTasksAsync(cancellationToken);
        if (existingTasks.Count != 0)
        {
            return;
        }

        var bills = await billsQueryService.GetBillsAsync(cancellationToken);
        var acts = await actsQueryService.GetActsAsync(cancellationToken);

        if (bills.Count != 0)
        {
            var bill = bills[0];
            await commandService.RequestAsync(new RequestAiEnrichmentCommand(
                Guid.Parse("F6744E80-95F5-4E03-9AB6-071D9B7B52F3"),
                "bill-summary",
                "bill",
                bill.Id,
                bill.Title,
                $"Podsumuj projekt ustawy \"{bill.Title}\". Zrodlo: {bill.SourceUrl}"), cancellationToken);
        }

        if (acts.Count != 0)
        {
            var act = acts[0];
            await commandService.RequestAsync(new RequestAiEnrichmentCommand(
                Guid.Parse("A555FF2D-1265-4D98-BF10-9018D46D0C6D"),
                "act-summary",
                "act",
                act.Id,
                act.Title,
                $"Podsumuj opublikowany akt \"{act.Title}\". Zrodlo: {act.Eli}"), cancellationToken);
        }

    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
