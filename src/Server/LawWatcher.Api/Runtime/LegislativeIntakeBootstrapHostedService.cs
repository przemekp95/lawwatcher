using LawWatcher.LegislativeIntake.Application;

namespace LawWatcher.Api.Runtime;

public sealed class LegislativeIntakeBootstrapHostedService(
    BillsQueryService queryService,
    LegislativeIntakeCommandService commandService) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var existingBills = await queryService.GetBillsAsync(cancellationToken);
        if (existingBills.Count != 0)
        {
            return;
        }

        var citBillId = Guid.Parse("7D5A85F1-7A03-4FD8-B3C0-54A110467259");
        await commandService.RegisterAsync(new RegisterBillCommand(
            citBillId,
            "sejm",
            "X-310",
            "https://www.sejm.gov.pl/druk/X-310",
            "Ustawa o zmianie CIT",
            new DateOnly(2026, 03, 24)), cancellationToken);
        await commandService.AttachDocumentAsync(new AttachBillDocumentCommand(
            citBillId,
            "draft",
            "bills/X-310/original.pdf"), cancellationToken);

        var vatBillId = Guid.Parse("D8704FD8-91A0-477C-B7B4-A8FA7A12D526");
        await commandService.RegisterAsync(new RegisterBillCommand(
            vatBillId,
            "sejm",
            "X-311",
            "https://www.sejm.gov.pl/druk/X-311",
            "Ustawa o zmianie VAT",
            new DateOnly(2026, 03, 25)), cancellationToken);
        await commandService.AttachDocumentAsync(new AttachBillDocumentCommand(
            vatBillId,
            "opinion",
            "bills/X-311/opinion.pdf"), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
