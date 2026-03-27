using System.Security.Cryptography;
using System.Text;
using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegislativeIntake.Application;
using LawWatcher.LegislativeProcess.Application;
using LawWatcher.Notifications.Application;
using LawWatcher.SearchAndDiscovery.Application;
using LawWatcher.SearchAndDiscovery.Domain;
using LawWatcher.TaxonomyAndProfiles.Application;

namespace LawWatcher.Worker.Lite;

public sealed record SearchProjectionRefreshResult(int DocumentCount, bool HasRebuilt);

public sealed class SearchProjectionRefreshService(
    BillsQueryService billsQueryService,
    ProcessesQueryService processesQueryService,
    ActsQueryService actsQueryService,
    MonitoringProfilesQueryService profilesQueryService,
    AlertsQueryService alertsQueryService,
    SearchIndexingService searchIndexingService)
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string? _lastFingerprint;

    public async Task<SearchProjectionRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        var bills = await billsQueryService.GetBillsAsync(cancellationToken);
        var processes = await processesQueryService.GetProcessesAsync(cancellationToken);
        var acts = await actsQueryService.GetActsAsync(cancellationToken);
        var profiles = await profilesQueryService.GetProfilesAsync(cancellationToken);
        var alerts = await alertsQueryService.GetAlertsAsync(cancellationToken);

        var documents = bills
            .Select(bill => new SearchSourceDocument(
                $"bill:{bill.Id:D}",
                bill.Title,
                SearchDocumentKind.Bill,
                $"Projekt {bill.ExternalId} ze zrodla {bill.SourceSystem}. Dokumenty: {FormatList(bill.DocumentKinds)}.",
                [bill.ExternalId, bill.SourceSystem, .. bill.DocumentKinds]))
            .Concat(acts.Select(act => new SearchSourceDocument(
                $"act:{act.Id:D}",
                act.Title,
                SearchDocumentKind.Act,
                $"Opublikowany akt prawny. ELI: {act.Eli}. Artefakty: {FormatList(act.ArtifactKinds)}.",
                [act.BillExternalId, act.Eli, .. act.ArtifactKinds])))
            .Concat(processes.Select(process => new SearchSourceDocument(
                $"process:{process.Id:D}",
                process.BillTitle,
                SearchDocumentKind.Process,
                $"Proces legislacyjny. Biezacy etap: {process.CurrentStageLabel} ({process.CurrentStageCode}).",
                [process.BillExternalId, process.CurrentStageCode, process.CurrentStageLabel, .. process.Stages.Select(stage => stage.Code)])))
            .Concat(profiles.Select(profile => new SearchSourceDocument(
                $"profile:{profile.Id:D}",
                profile.Name,
                SearchDocumentKind.Profile,
                $"Profil monitoringu. Slowa kluczowe: {FormatList(profile.Keywords)}. Polityka alertow: {profile.AlertPolicy}.",
                profile.Keywords)))
            .Concat(alerts.Select(alert => new SearchSourceDocument(
                $"alert:{alert.Id:D}",
                alert.BillTitle,
                SearchDocumentKind.Alert,
                $"Alert dla profilu {alert.ProfileName}. Dopasowane slowa: {FormatList(alert.MatchedKeywords)}.",
                [alert.ProfileName, alert.BillExternalId, .. alert.MatchedKeywords])))
            .ToArray();

        var currentFingerprint = ComputeFingerprint(documents);
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(_lastFingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                return new SearchProjectionRefreshResult(documents.Length, HasRebuilt: false);
            }

            await searchIndexingService.ReplaceAllAsync(documents, cancellationToken);
            _lastFingerprint = currentFingerprint;
            return new SearchProjectionRefreshResult(documents.Length, HasRebuilt: true);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static string ComputeFingerprint(IReadOnlyCollection<SearchSourceDocument> documents)
    {
        var builder = new StringBuilder();
        foreach (var document in documents.OrderBy(document => document.Id, StringComparer.OrdinalIgnoreCase))
        {
            builder
                .Append(document.Id).Append('|')
                .Append(document.Title).Append('|')
                .Append(document.Kind).Append('|')
                .Append(document.Snippet).Append('|')
                .AppendJoin(',', document.Keywords.OrderBy(keyword => keyword, StringComparer.OrdinalIgnoreCase))
                .AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string FormatList(IReadOnlyCollection<string> values) =>
        values.Count == 0 ? "brak" : string.Join(", ", values);
}
