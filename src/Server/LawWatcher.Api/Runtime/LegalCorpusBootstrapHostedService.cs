using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegislativeIntake.Application;
using LawWatcher.BuildingBlocks.Ports;
using System.Text;

namespace LawWatcher.Api.Runtime;

public sealed class LegalCorpusBootstrapHostedService(
    ActsQueryService actsQueryService,
    BillsQueryService billsQueryService,
    IDocumentStore documentStore,
    LegalCorpusCommandService commandService) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await StoreSeedArtifactAsync(
            documentStore,
            "acts/DU/2026/501/text.txt",
            "Ustawa o zmianie ustawy o CIT. Dokument zrodlowy przewiduje wejscie w zycie 1 kwietnia 2026 roku oraz zmiany w zaliczkach CIT.",
            cancellationToken);
        await StoreSeedArtifactAsync(
            documentStore,
            "acts/DU/2026/502/text.txt",
            "Ustawa o zmianie ustawy o VAT. Dokument zrodlowy przewiduje wejscie w zycie 5 kwietnia 2026 roku oraz dostosowanie obowiazkow JPK.",
            cancellationToken);

        var existingActs = await actsQueryService.GetActsAsync(cancellationToken);
        var bills = await billsQueryService.GetBillsAsync(cancellationToken);
        var citBill = bills.Single(bill => bill.ExternalId == "X-310");
        var vatBill = bills.Single(bill => bill.ExternalId == "X-311");

        var citEli = "https://eli.gov.pl/eli/DU/2026/501/ogl";
        var citArtifactObjectKey = "acts/DU/2026/501/text.txt";
        var citSeededAct = existingActs.SingleOrDefault(act => string.Equals(act.Eli, citEli, StringComparison.OrdinalIgnoreCase));
        var citActId = citSeededAct?.Id ?? Guid.Parse("4F7610ED-6E4C-432F-8D95-1B3BE1E0DDBF");
        if (citSeededAct is null)
        {
            await commandService.RegisterAsync(new RegisterActCommand(
                citActId,
                citBill.Id,
                citBill.Title,
                citBill.ExternalId,
                citEli,
                "Ustawa z dnia 28 marca 2026 r. o zmianie ustawy o CIT",
                new DateOnly(2026, 03, 28),
                new DateOnly(2026, 04, 01)), cancellationToken);
        }
        await commandService.AttachArtifactAsync(new AttachActArtifactCommand(
            citActId,
            "text",
            citArtifactObjectKey), cancellationToken);

        var vatEli = "https://eli.gov.pl/eli/DU/2026/502/ogl";
        var vatArtifactObjectKey = "acts/DU/2026/502/text.txt";
        var vatSeededAct = existingActs.SingleOrDefault(act => string.Equals(act.Eli, vatEli, StringComparison.OrdinalIgnoreCase));
        var vatActId = vatSeededAct?.Id ?? Guid.Parse("AA5177A4-D0A4-4B1E-8460-F7DDC6B01E89");
        if (vatSeededAct is null)
        {
            await commandService.RegisterAsync(new RegisterActCommand(
                vatActId,
                vatBill.Id,
                vatBill.Title,
                vatBill.ExternalId,
                vatEli,
                "Ustawa z dnia 29 marca 2026 r. o zmianie ustawy o VAT",
                new DateOnly(2026, 03, 29),
                new DateOnly(2026, 04, 05)), cancellationToken);
        }
        await commandService.AttachArtifactAsync(new AttachActArtifactCommand(
            vatActId,
            "text",
            vatArtifactObjectKey), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task StoreSeedArtifactAsync(
        IDocumentStore documentStore,
        string objectKey,
        string content,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(bytes, writable: false);
        await documentStore.PutAsync(
            new DocumentWriteRequest(
                LegalCorpusArtifactStorage.Bucket,
                objectKey,
                LegalCorpusArtifactStorage.GuessContentType(objectKey),
                stream),
            cancellationToken);
    }
}
