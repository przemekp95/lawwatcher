using System.Net.Http.Json;
using LawWatcher.AiEnrichment.Contracts;
using LawWatcher.IntegrationApi.Contracts;
using LawWatcher.LegalCorpus.Contracts;
using LawWatcher.LegislativeIntake.Contracts;
using LawWatcher.LegislativeProcess.Contracts;
using LawWatcher.Notifications.Contracts;
using LawWatcher.SearchAndDiscovery.Contracts;
using LawWatcher.TaxonomyAndProfiles.Contracts;

namespace LawWatcher.Portal.Services;

public sealed class PortalApiOptions
{
    public string BaseUrl { get; init; } = "http://127.0.0.1:5290";

    public Uri GetBaseUri()
    {
        var configuredBaseUrl = BaseUrl.Trim();
        if (string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            configuredBaseUrl = "http://127.0.0.1:5290";
        }

        if (!configuredBaseUrl.EndsWith("/", StringComparison.Ordinal))
        {
            configuredBaseUrl += "/";
        }

        return Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var uri)
            ? uri
            : new Uri("http://127.0.0.1:5290/", UriKind.Absolute);
    }
}

public sealed record PortalApiWarning(string Source, string Message);

public sealed record PortalDashboardData(
    SystemCapabilitiesResponse Capabilities,
    IReadOnlyCollection<MonitoringProfileResponse> Profiles,
    IReadOnlyCollection<BillSummaryResponse> Bills,
    IReadOnlyCollection<LegislativeProcessResponse> Processes,
    IReadOnlyCollection<ActSummaryResponse> Acts,
    IReadOnlyCollection<BillAlertResponse> Alerts,
    IReadOnlyCollection<EventFeedResponse> Events,
    IReadOnlyCollection<AiEnrichmentTaskResponse> AiTasks,
    IReadOnlyCollection<PortalApiWarning> Warnings)
{
    public string RuntimeProfile => Capabilities.RuntimeProfile;

    public int ProfileCount => Profiles.Count;

    public int BillCount => Bills.Count;

    public int ProcessCount => Processes.Count;

    public int ActCount => Acts.Count;

    public int AlertCount => Alerts.Count;

    public int ActiveAiTaskCount => AiTasks.Count(static task =>
        !string.Equals(task.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(task.Status, "failed", StringComparison.OrdinalIgnoreCase));
}

public sealed record PortalActivityData(
    IReadOnlyCollection<BillAlertResponse> Alerts,
    IReadOnlyCollection<EventFeedResponse> Events,
    IReadOnlyCollection<PortalApiWarning> Warnings);

public sealed record PortalSearchData(
    string Query,
    SearchBackend Backend,
    IReadOnlyCollection<SearchHitResponse> Hits,
    IReadOnlyCollection<PortalApiWarning> Warnings)
{
    public static PortalSearchData Empty { get; } = new(
        string.Empty,
        SearchBackend.ProjectionIndex,
        Array.Empty<SearchHitResponse>(),
        Array.Empty<PortalApiWarning>());
}

public sealed class LawWatcherPortalApiClient
{
    private readonly HttpClient _httpClient;

    public LawWatcherPortalApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<PortalDashboardData> GetDashboardAsync(CancellationToken cancellationToken)
    {
        var capabilitiesTask = GetRecordAsync(
            "v1/system/capabilities",
            "Runtime capabilities",
            CreateFallbackCapabilities,
            cancellationToken);
        var profilesTask = GetCollectionAsync<MonitoringProfileResponse>(
            "v1/profiles",
            "Monitoring profiles",
            cancellationToken);
        var billsTask = GetCollectionAsync<BillSummaryResponse>(
            "v1/bills",
            "Bills",
            cancellationToken);
        var processesTask = GetCollectionAsync<LegislativeProcessResponse>(
            "v1/processes",
            "Legislative processes",
            cancellationToken);
        var actsTask = GetCollectionAsync<ActSummaryResponse>(
            "v1/acts",
            "Published acts",
            cancellationToken);
        var alertsTask = GetCollectionAsync<BillAlertResponse>(
            "v1/alerts",
            "Alerts",
            cancellationToken);
        var eventsTask = GetCollectionAsync<EventFeedResponse>(
            "v1/events",
            "Event feed",
            cancellationToken);
        var aiTasksTask = GetCollectionAsync<AiEnrichmentTaskResponse>(
            "v1/ai/tasks",
            "AI tasks",
            cancellationToken);

        await Task.WhenAll(
            capabilitiesTask,
            profilesTask,
            billsTask,
            processesTask,
            actsTask,
            alertsTask,
            eventsTask,
            aiTasksTask);

        var warnings = CombineWarnings(
            await capabilitiesTask,
            await profilesTask,
            await billsTask,
            await processesTask,
            await actsTask,
            await alertsTask,
            await eventsTask,
            await aiTasksTask);

        return new PortalDashboardData(
            (await capabilitiesTask).Value,
            OrderProfiles((await profilesTask).Items),
            OrderBills((await billsTask).Items),
            OrderProcesses((await processesTask).Items),
            OrderActs((await actsTask).Items),
            OrderAlerts((await alertsTask).Items),
            OrderEvents((await eventsTask).Items),
            OrderAiTasks((await aiTasksTask).Items),
            warnings);
    }

    public async Task<PortalActivityData> GetActivityAsync(CancellationToken cancellationToken)
    {
        var alertsTask = GetCollectionAsync<BillAlertResponse>(
            "v1/alerts",
            "Alerts",
            cancellationToken);
        var eventsTask = GetCollectionAsync<EventFeedResponse>(
            "v1/events",
            "Event feed",
            cancellationToken);

        await Task.WhenAll(alertsTask, eventsTask);

        return new PortalActivityData(
            OrderAlerts((await alertsTask).Items),
            OrderEvents((await eventsTask).Items),
            CombineWarnings(await alertsTask, await eventsTask));
    }

    public async Task<PortalSearchData> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return PortalSearchData.Empty;
        }

        var encodedQuery = Uri.EscapeDataString(query.Trim());
        var result = await GetRecordAsync(
            $"v1/search?q={encodedQuery}",
            "Search",
            () => new SearchQueryResponse(query.Trim(), SearchBackend.ProjectionIndex, Array.Empty<SearchHitResponse>()),
            cancellationToken);

        return new PortalSearchData(
            result.Value.Query,
            result.Value.Backend,
            result.Value.Hits,
            result.Warnings);
    }

    private async Task<RecordReadResult<T>> GetRecordAsync<T>(
        string relativePath,
        string source,
        Func<T> fallbackFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(relativePath, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return RecordReadResult<T>.FromWarning(
                    fallbackFactory(),
                    source,
                    $"Request to '{relativePath}' returned {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
            return payload is not null
                ? RecordReadResult<T>.Success(payload)
                : RecordReadResult<T>.FromWarning(
                    fallbackFactory(),
                    source,
                    $"Request to '{relativePath}' returned an empty response body.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return RecordReadResult<T>.FromWarning(
                fallbackFactory(),
                source,
                $"Request to '{relativePath}' failed: {exception.Message}");
        }
    }

    private async Task<CollectionReadResult<T>> GetCollectionAsync<T>(
        string relativePath,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(relativePath, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return CollectionReadResult<T>.FromWarning(
                    Array.Empty<T>(),
                    source,
                    $"Request to '{relativePath}' returned {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<List<T>>(cancellationToken: cancellationToken);
            return payload is not null
                ? CollectionReadResult<T>.Success(payload)
                : CollectionReadResult<T>.FromWarning(
                    Array.Empty<T>(),
                    source,
                    $"Request to '{relativePath}' returned an empty response body.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CollectionReadResult<T>.FromWarning(
                Array.Empty<T>(),
                source,
                $"Request to '{relativePath}' failed: {exception.Message}");
        }
    }

    private static SystemCapabilitiesResponse CreateFallbackCapabilities() => new(
        "unavailable",
        new AiCapabilityResponse(false, "on-demand", 0, 0),
        new SearchCapabilityResponse(false, false, false, SearchBackend.ProjectionIndex),
        false,
        false);

    private static IReadOnlyCollection<PortalApiWarning> CombineWarnings(params IPortalReadResult[] results) =>
        results.SelectMany(static result => result.Warnings).ToArray();

    private static IReadOnlyCollection<MonitoringProfileResponse> OrderProfiles(IReadOnlyCollection<MonitoringProfileResponse> profiles) =>
        profiles.OrderBy(static profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    private static IReadOnlyCollection<BillSummaryResponse> OrderBills(IReadOnlyCollection<BillSummaryResponse> bills) =>
        bills.OrderByDescending(static bill => bill.SubmittedOn).ThenBy(static bill => bill.ExternalId, StringComparer.OrdinalIgnoreCase).ToArray();

    private static IReadOnlyCollection<LegislativeProcessResponse> OrderProcesses(IReadOnlyCollection<LegislativeProcessResponse> processes) =>
        processes.OrderByDescending(static process => process.LastUpdatedOn).ThenBy(static process => process.BillExternalId, StringComparer.OrdinalIgnoreCase).ToArray();

    private static IReadOnlyCollection<ActSummaryResponse> OrderActs(IReadOnlyCollection<ActSummaryResponse> acts) =>
        acts.OrderByDescending(static act => act.PublishedOn).ThenBy(static act => act.BillExternalId, StringComparer.OrdinalIgnoreCase).ToArray();

    private static IReadOnlyCollection<BillAlertResponse> OrderAlerts(IReadOnlyCollection<BillAlertResponse> alerts) =>
        alerts.OrderByDescending(static alert => alert.CreatedAtUtc).ThenBy(static alert => alert.BillExternalId, StringComparer.OrdinalIgnoreCase).ToArray();

    private static IReadOnlyCollection<EventFeedResponse> OrderEvents(IReadOnlyCollection<EventFeedResponse> events) =>
        events.OrderByDescending(static item => item.OccurredAtUtc).ToArray();

    private static IReadOnlyCollection<AiEnrichmentTaskResponse> OrderAiTasks(IReadOnlyCollection<AiEnrichmentTaskResponse> tasks) =>
        tasks.OrderByDescending(static task => task.RequestedAtUtc).ToArray();

    private interface IPortalReadResult
    {
        IReadOnlyCollection<PortalApiWarning> Warnings { get; }
    }

    private sealed record RecordReadResult<T>(T Value, IReadOnlyCollection<PortalApiWarning> Warnings) : IPortalReadResult
    {
        public static RecordReadResult<T> Success(T value) => new(value, Array.Empty<PortalApiWarning>());

        public static RecordReadResult<T> FromWarning(T value, string source, string message) =>
            new(value, new[] { new PortalApiWarning(source, message) });
    }

    private sealed record CollectionReadResult<T>(IReadOnlyCollection<T> Items, IReadOnlyCollection<PortalApiWarning> Warnings) : IPortalReadResult
    {
        public static CollectionReadResult<T> Success(IReadOnlyCollection<T> items) => new(items, Array.Empty<PortalApiWarning>());

        public static CollectionReadResult<T> FromWarning(IReadOnlyCollection<T> items, string source, string message) =>
            new(items, new[] { new PortalApiWarning(source, message) });
    }
}
