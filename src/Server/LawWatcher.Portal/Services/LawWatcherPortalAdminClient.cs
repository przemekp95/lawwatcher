using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LawWatcher.IdentityAndAccess.Contracts;
using LawWatcher.IntegrationApi.Contracts;
using LawWatcher.TaxonomyAndProfiles.Contracts;
using Microsoft.Extensions.Options;

namespace LawWatcher.Portal.Services;

public sealed class PortalAdminApiException(string message, int statusCode) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class LawWatcherPortalAdminClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClientHandler _handler;
    private readonly HttpClient _httpClient;
    private string? _csrfRequestToken;

    public LawWatcherPortalAdminClient(IOptions<PortalApiOptions> options)
    {
        _handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true
        };
        _httpClient = new HttpClient(_handler)
        {
            BaseAddress = options.Value.GetBaseUri(),
            Timeout = TimeSpan.FromSeconds(15)
        };
        CurrentSession = AnonymousSession;
    }

    public OperatorSessionResponse CurrentSession { get; private set; }

    public event Action? SessionChanged;

    public async Task<OperatorSessionResponse> GetSessionAsync(CancellationToken cancellationToken)
    {
        CurrentSession = await SendAsync<OperatorSessionResponse>(
            HttpMethod.Get,
            "v1/operator/session",
            cancellationToken: cancellationToken);
        _csrfRequestToken = CurrentSession.CsrfRequestToken;
        SessionChanged?.Invoke();
        return CurrentSession;
    }

    public async Task<OperatorSessionResponse> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        CurrentSession = await SendAsync<OperatorSessionResponse>(
            HttpMethod.Post,
            "v1/operator/login",
            new OperatorLoginRequest(email, password),
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

        _csrfRequestToken = CurrentSession.CsrfRequestToken;
        SessionChanged?.Invoke();
        return CurrentSession;
    }

    public async Task<OperatorSessionResponse> LogoutAsync(CancellationToken cancellationToken)
    {
        CurrentSession = await SendAsync<OperatorSessionResponse>(
            HttpMethod.Post,
            "v1/operator/logout",
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

        _csrfRequestToken = CurrentSession.CsrfRequestToken;
        SessionChanged?.Invoke();
        return CurrentSession;
    }

    public async Task<IReadOnlyList<OperatorAccountResponse>> GetOperatorsAsync(CancellationToken cancellationToken)
    {
        var operators = await SendAsync<List<OperatorAccountResponse>>(
            HttpMethod.Get,
            "v1/operators",
            cancellationToken: cancellationToken);
        return operators
            .OrderBy(@operator => @operator.Email, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<AcceptedCommandResponse> CreateOperatorAsync(CreateOperatorAccountRequest request, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Post,
            "v1/operators",
            request,
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public Task<AcceptedCommandResponse> UpdateOperatorAsync(Guid operatorId, UpdateOperatorAccountRequest request, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Patch,
            $"v1/operators/{operatorId:D}",
            request,
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public Task<AcceptedCommandResponse> DeactivateOperatorAsync(Guid operatorId, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Post,
            $"v1/operators/{operatorId:D}/deactivate",
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public Task<AcceptedCommandResponse> ResetOperatorPasswordAsync(Guid operatorId, ResetOperatorPasswordRequest request, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Post,
            $"v1/operators/{operatorId:D}/reset-password",
            request,
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<MonitoringProfileResponse>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        var profiles = await SendAsync<List<MonitoringProfileResponse>>(
            HttpMethod.Get,
            "v1/profiles",
            cancellationToken: cancellationToken);
        return profiles
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<AcceptedCommandResponse> CreateProfileAsync(CreateMonitoringProfileRequest request, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Post,
            "v1/profiles",
            request,
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public Task<AcceptedCommandResponse> AddProfileRuleAsync(Guid profileId, AddMonitoringProfileRuleRequest request, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Post,
            $"v1/profiles/{profileId:D}/rules",
            request,
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public Task<AcceptedCommandResponse> ChangeProfileAlertPolicyAsync(Guid profileId, ChangeMonitoringProfileAlertPolicyRequest request, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Patch,
            $"v1/profiles/{profileId:D}/alert-policy",
            request,
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public Task<AcceptedCommandResponse> DeactivateProfileAsync(Guid profileId, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Delete,
            $"v1/profiles/{profileId:D}",
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<ProfileSubscriptionResponse>> GetSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var subscriptions = await SendAsync<List<ProfileSubscriptionResponse>>(
            HttpMethod.Get,
            "v1/subscriptions",
            cancellationToken: cancellationToken);
        return subscriptions
            .OrderBy(subscription => subscription.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(subscription => subscription.Subscriber, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<AcceptedCommandResponse> CreateSubscriptionAsync(CreateProfileSubscriptionRequest request, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Post,
            "v1/subscriptions",
            request,
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public Task<AcceptedCommandResponse> ChangeSubscriptionAlertPolicyAsync(Guid subscriptionId, ChangeProfileSubscriptionAlertPolicyRequest request, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Patch,
            $"v1/subscriptions/{subscriptionId:D}/alert-policy",
            request,
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public Task<AcceptedCommandResponse> DeactivateSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Delete,
            $"v1/subscriptions/{subscriptionId:D}",
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<WebhookRegistrationResponse>> GetWebhooksAsync(CancellationToken cancellationToken)
    {
        var webhooks = await SendAsync<List<WebhookRegistrationResponse>>(
            HttpMethod.Get,
            "v1/webhooks",
            cancellationToken: cancellationToken);
        return webhooks
            .OrderByDescending(webhook => webhook.IsActive)
            .ThenBy(webhook => webhook.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<AcceptedCommandResponse> CreateWebhookAsync(CreateWebhookRegistrationRequest request, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Post,
            "v1/webhooks",
            request,
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public Task<AcceptedCommandResponse> UpdateWebhookAsync(Guid webhookId, UpdateWebhookRegistrationRequest request, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Patch,
            $"v1/webhooks/{webhookId:D}",
            request,
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public Task<AcceptedCommandResponse> DeactivateWebhookAsync(Guid webhookId, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Delete,
            $"v1/webhooks/{webhookId:D}",
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<ApiClientResponse>> GetApiClientsAsync(CancellationToken cancellationToken)
    {
        var apiClients = await SendAsync<List<ApiClientResponse>>(
            HttpMethod.Get,
            "v1/api-clients",
            cancellationToken: cancellationToken);
        return apiClients
            .OrderBy(client => client.ClientIdentifier, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<AcceptedCommandResponse> CreateApiClientAsync(CreateApiClientRequest request, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Post,
            "v1/api-clients",
            request,
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public Task<AcceptedCommandResponse> UpdateApiClientAsync(Guid apiClientId, UpdateApiClientRequest request, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Patch,
            $"v1/api-clients/{apiClientId:D}",
            request,
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public Task<AcceptedCommandResponse> DeactivateApiClientAsync(Guid apiClientId, CancellationToken cancellationToken) =>
        SendAsync<AcceptedCommandResponse>(
            HttpMethod.Delete,
            $"v1/api-clients/{apiClientId:D}",
            requireAntiforgery: true,
            cancellationToken: cancellationToken);

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    private async Task EnsureSessionAsync(CancellationToken cancellationToken)
    {
        await GetSessionAsync(cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? payload = null,
        bool requireAntiforgery = false,
        CancellationToken cancellationToken = default)
    {
        if (requireAntiforgery)
        {
            await EnsureSessionAsync(cancellationToken);
        }

        using var request = new HttpRequestMessage(method, relativePath);
        if (requireAntiforgery && !string.IsNullOrWhiteSpace(_csrfRequestToken))
        {
            request.Headers.TryAddWithoutValidation("X-LawWatcher-CSRF", _csrfRequestToken);
        }

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: JsonOptions);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, cancellationToken);
        }

        if (typeof(T) == typeof(string))
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return (T)(object)content;
        }

        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        if (result is null)
        {
            throw new PortalAdminApiException($"Request to '{relativePath}' returned an empty body.", (int)response.StatusCode);
        }

        return result;
    }

    private static async Task<PortalAdminApiException> CreateExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string message;
        try
        {
            var details = await response.Content.ReadFromJsonAsync<PortalProblemDetails>(JsonOptions, cancellationToken);
            message = details switch
            {
                { Detail.Length: > 0 } => details.Detail,
                { Title.Length: > 0 } => details.Title,
                _ => $"Request failed with status {(int)response.StatusCode}."
            };
        }
        catch
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            message = string.IsNullOrWhiteSpace(raw)
                ? $"Request failed with status {(int)response.StatusCode}."
                : raw;
        }

        return new PortalAdminApiException(message, (int)response.StatusCode);
    }

    private static readonly OperatorSessionResponse AnonymousSession = new(
        false,
        null,
        null,
        null,
        Array.Empty<string>(),
        string.Empty);

    private sealed record PortalProblemDetails(string Title, string Detail);
}
