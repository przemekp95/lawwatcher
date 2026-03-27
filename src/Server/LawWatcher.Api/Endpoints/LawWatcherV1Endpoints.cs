using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using LawWatcher.Api.Runtime;
using LawWatcher.AiEnrichment.Application;
using LawWatcher.AiEnrichment.Contracts;
using LawWatcher.IdentityAndAccess.Application;
using LawWatcher.IdentityAndAccess.Contracts;
using LawWatcher.IntegrationApi.Application;
using LawWatcher.IntegrationApi.Contracts;
using LawWatcher.IntegrationApi.Domain.Backfills;
using LawWatcher.IntegrationApi.Domain.Replays;
using LawWatcher.LegalCorpus.Application;
using LawWatcher.LegislativeIntake.Application;
using LawWatcher.LegislativeProcess.Application;
using LawWatcher.Notifications.Application;
using LawWatcher.SearchAndDiscovery.Application;
using LawWatcher.TaxonomyAndProfiles.Application;
using LawWatcher.TaxonomyAndProfiles.Contracts;
using LawWatcher.TaxonomyAndProfiles.Domain.MonitoringProfiles;
using LawWatcher.TaxonomyAndProfiles.Domain.Subscriptions;

namespace LawWatcher.Api.Endpoints;

public static class LawWatcherV1Endpoints
{
    public static IEndpointRouteBuilder MapLawWatcherV1Endpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapGet("/system/capabilities", (ISystemCapabilitiesProvider provider) =>
            TypedResults.Ok(SystemCapabilitiesResponseFactory.Create(provider.Current)));

        v1.MapGet("/search", async (string? q, ISystemCapabilitiesProvider provider, SearchQueryService queryService, CancellationToken cancellationToken) =>
            TypedResults.Ok(SystemCapabilitiesResponseFactory.CreateSearchResponse(
                await queryService.SearchAsync(q ?? string.Empty, cancellationToken),
                provider.Current)));
        v1.MapGet("/ai/tasks", async (AiEnrichmentTasksQueryService queryService, CancellationToken cancellationToken) =>
            TypedResults.Ok(await queryService.GetTasksAsync(cancellationToken)));
        v1.MapGet("/operator/session", async (
            HttpContext httpContext,
            IAntiforgery antiforgery,
            OperatorAccountsQueryService queryService,
            CancellationToken cancellationToken) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(httpContext);
            if (!TryExtractOperatorId(httpContext.User, out var operatorId))
            {
                return TypedResults.Ok(new OperatorSessionResponse(
                    false,
                    null,
                    null,
                    null,
                    Array.Empty<string>(),
                    tokens.RequestToken ?? string.Empty));
            }

            var @operator = await queryService.GetOperatorAsync(operatorId, cancellationToken);
            if (@operator is null || !@operator.IsActive)
            {
                await httpContext.SignOutAsync(OperatorCookieAuthenticationDefaults.Scheme);
                return TypedResults.Ok(new OperatorSessionResponse(
                    false,
                    null,
                    null,
                    null,
                    Array.Empty<string>(),
                    tokens.RequestToken ?? string.Empty));
            }

            return TypedResults.Ok(new OperatorSessionResponse(
                true,
                @operator.Id,
                @operator.Email,
                @operator.DisplayName,
                @operator.Permissions,
                tokens.RequestToken ?? string.Empty));
        });
        v1.MapPost("/operator/login", async (
            HttpContext httpContext,
            IAntiforgery antiforgery,
            OperatorLoginRequest request,
            OperatorAuthenticationService authenticationService,
            CancellationToken cancellationToken) =>
        {
            var csrfValidation = await ValidateAntiforgeryAsync(httpContext, antiforgery);
            if (csrfValidation is not null)
            {
                return csrfValidation;
            }

            var authentication = await authenticationService.AuthenticateAsync(
                request.Email,
                request.Password,
                cancellationToken);
            if (authentication.Decision is not OperatorAuthenticationDecision.Authorized ||
                authentication.OperatorId is null ||
                authentication.Email is null ||
                authentication.DisplayName is null)
            {
                return TypedResults.Unauthorized();
            }

            var principal = CreateOperatorPrincipal(authentication);
            await httpContext.SignInAsync(
                OperatorCookieAuthenticationDefaults.Scheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    AllowRefresh = true
                });

            var tokens = antiforgery.GetAndStoreTokens(httpContext);
            return TypedResults.Ok(new OperatorSessionResponse(
                true,
                authentication.OperatorId,
                authentication.Email,
                authentication.DisplayName,
                authentication.Permissions,
                tokens.RequestToken ?? string.Empty));
        });
        v1.MapPost("/operator/logout", async (
            HttpContext httpContext,
            IAntiforgery antiforgery) =>
        {
            var csrfValidation = await ValidateAntiforgeryAsync(httpContext, antiforgery);
            if (csrfValidation is not null)
            {
                return csrfValidation;
            }

            await httpContext.SignOutAsync(OperatorCookieAuthenticationDefaults.Scheme);
            var tokens = antiforgery.GetAndStoreTokens(httpContext);
            return TypedResults.Ok(new OperatorSessionResponse(
                false,
                null,
                null,
                null,
                Array.Empty<string>(),
                tokens.RequestToken ?? string.Empty));
        });
        v1.MapGet("/operator/me", async (
            HttpContext httpContext,
            IAntiforgery antiforgery,
            OperatorAccountsQueryService queryService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeOperatorAsync(
                httpContext,
                antiforgery,
                null,
                null,
                cancellationToken);
            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            var @operator = await queryService.GetOperatorAsync(authorization.OperatorId!.Value, cancellationToken);
            return @operator is null
                ? TypedResults.Unauthorized()
                : TypedResults.Ok(@operator);
        });
        v1.MapGet("/operators", async (
            HttpContext httpContext,
            IAntiforgery antiforgery,
            OperatorAccessService operatorAccessService,
            OperatorAccountsQueryService queryService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeOperatorAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                "operators:write",
                cancellationToken);
            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            return TypedResults.Ok(await queryService.GetOperatorsAsync(cancellationToken));
        });
        v1.MapPost("/operators", async (
            HttpContext httpContext,
            IAntiforgery antiforgery,
            CreateOperatorAccountRequest request,
            OperatorAccessService operatorAccessService,
            OperatorAccountsCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeOperatorAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                "operators:write",
                cancellationToken,
                requireAntiforgery: true);
            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            var operatorId = Guid.NewGuid();
            await commandService.RegisterAsync(new RegisterOperatorAccountCommand(
                operatorId,
                request.Email,
                request.DisplayName,
                request.Password,
                request.Permissions), cancellationToken);

            return TypedResults.Accepted(
                "/v1/operators",
                new AcceptedCommandResponse(operatorId, "active"));
        });
        v1.MapPatch("/operators/{operatorId:guid}", async (
            Guid operatorId,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            UpdateOperatorAccountRequest request,
            OperatorAccessService operatorAccessService,
            OperatorAccountsCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeOperatorAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                "operators:write",
                cancellationToken,
                requireAntiforgery: true);
            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            await commandService.UpdateAsync(new UpdateOperatorAccountCommand(
                operatorId,
                request.DisplayName,
                request.Permissions), cancellationToken);

            return TypedResults.Accepted(
                $"/v1/operators/{operatorId:D}",
                new AcceptedCommandResponse(operatorId, "updated"));
        });
        v1.MapPost("/operators/{operatorId:guid}/deactivate", async (
            Guid operatorId,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            OperatorAccessService operatorAccessService,
            OperatorAccountsCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeOperatorAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                "operators:write",
                cancellationToken,
                requireAntiforgery: true);
            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            await commandService.DeactivateAsync(new DeactivateOperatorAccountCommand(operatorId), cancellationToken);
            return TypedResults.Accepted(
                $"/v1/operators/{operatorId:D}",
                new AcceptedCommandResponse(operatorId, "deactivated"));
        });
        v1.MapPost("/operators/{operatorId:guid}/reset-password", async (
            Guid operatorId,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            ResetOperatorPasswordRequest request,
            OperatorAccessService operatorAccessService,
            OperatorAccountsCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeOperatorAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                "operators:write",
                cancellationToken,
                requireAntiforgery: true);
            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            await commandService.ResetPasswordAsync(new ResetOperatorPasswordCommand(operatorId, request.NewPassword), cancellationToken);
            return TypedResults.Accepted(
                $"/v1/operators/{operatorId:D}",
                new AcceptedCommandResponse(operatorId, "updated"));
        });
        v1.MapPost("/ai/tasks", async (
            HttpContext httpContext,
            CreateAiEnrichmentTaskRequest request,
            ApiClientAccessService accessService,
            AiEnrichmentCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeApiClientAsync(
                httpContext,
                accessService,
                "ai:write",
                cancellationToken);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            var command = new RequestAiEnrichmentCommand(
                Guid.NewGuid(),
                request.Kind,
                request.SubjectType,
                request.SubjectId,
                request.SubjectTitle,
                request.Prompt);
            await commandService.RequestAsync(command, cancellationToken);

            return TypedResults.Accepted(
                "/v1/ai/tasks",
                new AcceptedCommandResponse(command.TaskId, "queued"));
        });
        v1.MapGet("/system/notification-dispatches", async (AlertNotificationDispatchesQueryService queryService, CancellationToken cancellationToken) =>
            TypedResults.Ok(await queryService.GetDispatchesAsync(cancellationToken)));
        v1.MapGet("/system/webhook-dispatches", async (WebhookEventDispatchesQueryService queryService, CancellationToken cancellationToken) =>
            TypedResults.Ok(await queryService.GetDispatchesAsync(cancellationToken)));
        v1.MapGet("/system/messaging", async (
            HttpContext httpContext,
            IAntiforgery antiforgery,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            MessagingDiagnosticsQueryService queryService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "api-clients:write",
                cancellationToken);
            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            return TypedResults.Ok(await queryService.GetDiagnosticsAsync(cancellationToken));
        });
        v1.MapPost("/system/maintenance/retention", async (
            HttpContext httpContext,
            IAntiforgery antiforgery,
            RunRetentionMaintenanceRequest request,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            RetentionMaintenanceCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "api-clients:write",
                cancellationToken,
                requireAntiforgery: true);
            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            if (!commandService.IsAvailable)
            {
                return TypedResults.Problem(
                    title: "Retention maintenance unavailable",
                    detail: "Retention maintenance is only available in the SQL-backed runtime.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                return TypedResults.Ok(await commandService.RunAsync(
                    new RunRetentionMaintenanceCommand(
                        request.PublishedOutboxRetentionHours,
                        request.ProcessedInboxRetentionHours,
                        request.EventFeedRetentionHours,
                        request.SearchDocumentsRetentionHours),
                    cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return TypedResults.Problem(
                    title: "Invalid retention policy",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });
        v1.MapGet("/system/api-clients", async (
            HttpContext httpContext,
            IAntiforgery antiforgery,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            ApiClientsQueryService queryService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "api-clients:write",
                cancellationToken);
            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            return TypedResults.Ok(await queryService.GetApiClientsAsync(cancellationToken));
        });
        v1.MapGet("/api-clients", async (
            HttpContext httpContext,
            IAntiforgery antiforgery,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            ApiClientsQueryService queryService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "api-clients:write",
                cancellationToken);
            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            return TypedResults.Ok(await queryService.GetApiClientsAsync(cancellationToken));
        });

        v1.MapGet("/bills", async (BillsQueryService queryService, CancellationToken cancellationToken) =>
            TypedResults.Ok(await queryService.GetBillsAsync(cancellationToken)));
        v1.MapGet("/processes", async (ProcessesQueryService queryService, CancellationToken cancellationToken) =>
            TypedResults.Ok(await queryService.GetProcessesAsync(cancellationToken)));
        v1.MapGet("/acts", async (ActsQueryService queryService, CancellationToken cancellationToken) =>
            TypedResults.Ok(await queryService.GetActsAsync(cancellationToken)));
        v1.MapGet("/events", async (EventFeedQueryService queryService, CancellationToken cancellationToken) =>
            TypedResults.Ok(await queryService.GetEventsAsync(cancellationToken)));
        v1.MapGet("/profiles", async (MonitoringProfilesQueryService queryService, CancellationToken cancellationToken) =>
            TypedResults.Ok(await queryService.GetProfilesAsync(cancellationToken)));
        v1.MapPost("/profiles", async (
            HttpContext httpContext,
            IAntiforgery antiforgery,
            CreateMonitoringProfileRequest request,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            MonitoringProfilesCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "profiles:write",
                cancellationToken,
                requireAntiforgery: true);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            var profileId = Guid.NewGuid();
            await commandService.CreateAsync(
                new CreateMonitoringProfileCommand(
                    profileId,
                    request.Name,
                    ToAlertPolicy(request.AlertPolicy, request.DigestIntervalMinutes)),
                cancellationToken);

            foreach (var keyword in request.Keywords)
            {
                await commandService.AddRuleAsync(
                    new AddMonitoringProfileRuleCommand(
                        profileId,
                        ProfileRule.Keyword(keyword)),
                    cancellationToken);
            }

            return TypedResults.Accepted(
                "/v1/profiles",
                new AcceptedCommandResponse(profileId, "active"));
        });
        v1.MapPost("/profiles/{profileId:guid}/rules", async (
            Guid profileId,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            AddMonitoringProfileRuleRequest request,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            MonitoringProfilesCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "profiles:write",
                cancellationToken,
                requireAntiforgery: true);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            await commandService.AddRuleAsync(
                new AddMonitoringProfileRuleCommand(profileId, ProfileRule.Keyword(request.Keyword)),
                cancellationToken);

            return TypedResults.Accepted(
                $"/v1/profiles/{profileId:D}",
                new AcceptedCommandResponse(profileId, "updated"));
        });
        v1.MapPatch("/profiles/{profileId:guid}/alert-policy", async (
            Guid profileId,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            ChangeMonitoringProfileAlertPolicyRequest request,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            MonitoringProfilesCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "profiles:write",
                cancellationToken,
                requireAntiforgery: true);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            await commandService.ChangeAlertPolicyAsync(
                new ChangeMonitoringProfileAlertPolicyCommand(
                    profileId,
                    ToAlertPolicy(request.AlertPolicy, request.DigestIntervalMinutes)),
                cancellationToken);

            return TypedResults.Accepted(
                $"/v1/profiles/{profileId:D}",
                new AcceptedCommandResponse(profileId, "updated"));
        });
        v1.MapDelete("/profiles/{profileId:guid}", async (
            Guid profileId,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            MonitoringProfilesCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "profiles:write",
                cancellationToken,
                requireAntiforgery: true);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            await commandService.DeactivateAsync(
                new DeactivateMonitoringProfileCommand(profileId),
                cancellationToken);

            return TypedResults.Accepted(
                $"/v1/profiles/{profileId:D}",
                new AcceptedCommandResponse(profileId, "deactivated"));
        });
        v1.MapGet("/subscriptions", async (ProfileSubscriptionsQueryService queryService, CancellationToken cancellationToken) =>
            TypedResults.Ok(await queryService.GetSubscriptionsAsync(cancellationToken)));
        v1.MapPost("/subscriptions", async (
            HttpContext httpContext,
            IAntiforgery antiforgery,
            CreateProfileSubscriptionRequest request,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            ProfileSubscriptionsCommandService commandService,
            MonitoringProfilesQueryService profilesQueryService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "subscriptions:write",
                cancellationToken,
                requireAntiforgery: true);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            var profiles = await profilesQueryService.GetProfilesAsync(cancellationToken);
            var profile = profiles.SingleOrDefault(candidate => candidate.Id == request.ProfileId);
            if (profile is null)
            {
                return TypedResults.NotFound();
            }

            var subscriptionId = Guid.NewGuid();
            await commandService.CreateAsync(
                new CreateProfileSubscriptionCommand(
                    subscriptionId,
                    profile.Id,
                    profile.Name,
                    request.Subscriber,
                    ToSubscriptionChannel(request.Channel),
                    ToAlertPolicy(request.AlertPolicy, request.DigestIntervalMinutes)),
                cancellationToken);

            return TypedResults.Accepted(
                "/v1/subscriptions",
                new AcceptedCommandResponse(subscriptionId, "active"));
        });
        v1.MapPatch("/subscriptions/{subscriptionId:guid}/alert-policy", async (
            Guid subscriptionId,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            ChangeProfileSubscriptionAlertPolicyRequest request,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            ProfileSubscriptionsCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "subscriptions:write",
                cancellationToken,
                requireAntiforgery: true);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            await commandService.ChangeAlertPolicyAsync(
                new ChangeProfileSubscriptionAlertPolicyCommand(
                    subscriptionId,
                    ToAlertPolicy(request.AlertPolicy, request.DigestIntervalMinutes)),
                cancellationToken);

            return TypedResults.Accepted(
                $"/v1/subscriptions/{subscriptionId:D}",
                new AcceptedCommandResponse(subscriptionId, "updated"));
        });
        v1.MapDelete("/subscriptions/{subscriptionId:guid}", async (
            Guid subscriptionId,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            ProfileSubscriptionsCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "subscriptions:write",
                cancellationToken,
                requireAntiforgery: true);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            await commandService.DeactivateAsync(
                new DeactivateProfileSubscriptionCommand(subscriptionId),
                cancellationToken);

            return TypedResults.Accepted(
                $"/v1/subscriptions/{subscriptionId:D}",
                new AcceptedCommandResponse(subscriptionId, "deactivated"));
        });
        v1.MapGet("/alerts", async (AlertsQueryService queryService, CancellationToken cancellationToken) =>
            TypedResults.Ok(await queryService.GetAlertsAsync(cancellationToken)));
        v1.MapGet("/webhooks", async (WebhookRegistrationsQueryService queryService, CancellationToken cancellationToken) =>
            TypedResults.Ok(await queryService.GetWebhooksAsync(cancellationToken)));
        v1.MapPost("/webhooks", async (
            HttpContext httpContext,
            IAntiforgery antiforgery,
            CreateWebhookRegistrationRequest request,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            WebhookRegistrationsCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "webhooks:write",
                cancellationToken,
                requireAntiforgery: true);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            var command = new RegisterWebhookCommand(
                Guid.NewGuid(),
                request.Name,
                request.CallbackUrl,
                request.EventTypes);
            await commandService.RegisterAsync(command, cancellationToken);

            return TypedResults.Accepted(
                "/v1/webhooks",
                new AcceptedCommandResponse(command.RegistrationId, "active"));
        });
        v1.MapPatch("/webhooks/{registrationId:guid}", async (
            Guid registrationId,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            UpdateWebhookRegistrationRequest request,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            WebhookRegistrationsCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "webhooks:write",
                cancellationToken,
                requireAntiforgery: true);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            await commandService.UpdateAsync(
                new UpdateWebhookCommand(
                    registrationId,
                    request.Name,
                    request.CallbackUrl,
                    request.EventTypes),
                cancellationToken);

            return TypedResults.Accepted(
                $"/v1/webhooks/{registrationId:D}",
                new AcceptedCommandResponse(registrationId, "updated"));
        });
        v1.MapDelete("/webhooks/{registrationId:guid}", async (
            Guid registrationId,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            WebhookRegistrationsCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "webhooks:write",
                cancellationToken,
                requireAntiforgery: true);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            await commandService.DeactivateAsync(
                new DeactivateWebhookCommand(registrationId),
                cancellationToken);

            return TypedResults.Accepted(
                $"/v1/webhooks/{registrationId:D}",
                new AcceptedCommandResponse(registrationId, "deactivated"));
        });
        v1.MapPost("/api-clients", async (
            HttpContext httpContext,
            IAntiforgery antiforgery,
            CreateApiClientRequest request,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            ApiClientsCommandService commandService,
            IApiTokenFingerprintService tokenFingerprintService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "api-clients:write",
                cancellationToken,
                requireAntiforgery: true);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            var clientId = Guid.NewGuid();
            await commandService.RegisterAsync(
                new RegisterApiClientCommand(
                    clientId,
                    request.Name,
                    request.ClientIdentifier,
                    tokenFingerprintService.CreateFingerprint(request.Token),
                    request.Scopes),
                cancellationToken);

            return TypedResults.Accepted(
                "/v1/api-clients",
                new AcceptedCommandResponse(clientId, "active"));
        });
        v1.MapPatch("/api-clients/{apiClientId:guid}", async (
            Guid apiClientId,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            UpdateApiClientRequest request,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            ApiClientsCommandService commandService,
            IApiTokenFingerprintService tokenFingerprintService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "api-clients:write",
                cancellationToken,
                requireAntiforgery: true);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            await commandService.UpdateAsync(
                new UpdateApiClientCommand(
                    apiClientId,
                    request.Name,
                    string.IsNullOrWhiteSpace(request.Token) ? null : tokenFingerprintService.CreateFingerprint(request.Token),
                    request.Scopes),
                cancellationToken);

            return TypedResults.Accepted(
                $"/v1/api-clients/{apiClientId:D}",
                new AcceptedCommandResponse(apiClientId, "updated"));
        });
        v1.MapDelete("/api-clients/{apiClientId:guid}", async (
            Guid apiClientId,
            HttpContext httpContext,
            IAntiforgery antiforgery,
            ApiClientAccessService accessService,
            OperatorAccessService operatorAccessService,
            ApiClientsCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeAdminRequestAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                accessService,
                "api-clients:write",
                cancellationToken,
                requireAntiforgery: true);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            await commandService.DeactivateAsync(
                new DeactivateApiClientCommand(apiClientId),
                cancellationToken);

            return TypedResults.Accepted(
                $"/v1/api-clients/{apiClientId:D}",
                new AcceptedCommandResponse(apiClientId, "deactivated"));
        });
        v1.MapGet("/backfills", async (BackfillRequestsQueryService queryService, CancellationToken cancellationToken) =>
            TypedResults.Ok(await queryService.GetBackfillsAsync(cancellationToken)));
        v1.MapGet("/replays", async (ReplayRequestsQueryService queryService, CancellationToken cancellationToken) =>
            TypedResults.Ok(await queryService.GetReplaysAsync(cancellationToken)));
        v1.MapPost("/replays", async (
            HttpContext httpContext,
            CreateReplayRequest request,
            ApiClientAccessService accessService,
            ReplayRequestsCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeApiClientAsync(
                httpContext,
                accessService,
                "replays:write",
                cancellationToken);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            var accessResult = authorization.AccessResult!;
            var command = new RequestReplayCommand(
                Guid.NewGuid(),
                ReplayScope.Of(request.Scope),
                accessResult.ClientIdentifier!);
            await commandService.RequestAsync(command, cancellationToken);

            return TypedResults.Accepted(
                "/v1/replays",
                new AcceptedCommandResponse(command.ReplayRequestId, "queued"));
        });
        v1.MapPost("/backfills", async (
            HttpContext httpContext,
            CreateBackfillRequest request,
            ApiClientAccessService accessService,
            BackfillRequestsCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            var authorization = await AuthorizeApiClientAsync(
                httpContext,
                accessService,
                "backfills:write",
                cancellationToken);

            if (authorization.FailureResult is not null)
            {
                return authorization.FailureResult;
            }

            var accessResult = authorization.AccessResult!;
            var command = new RequestBackfillCommand(
                Guid.NewGuid(),
                BackfillSource.Of(request.Source),
                BackfillScope.Of(request.Scope),
                request.RequestedFrom,
                request.RequestedTo,
                accessResult.ClientIdentifier!);
            await commandService.RequestAsync(command, cancellationToken);

            return TypedResults.Accepted(
                "/v1/backfills",
                new AcceptedCommandResponse(command.BackfillRequestId, "queued"));
        });

        return app;
    }

    private static async Task<AdminAuthorizationOutcome> AuthorizeAdminRequestAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        OperatorAccessService operatorAccessService,
        ApiClientAccessService accessService,
        string requiredPermission,
        CancellationToken cancellationToken,
        bool requireAntiforgery = false)
    {
        if (TryExtractOperatorId(httpContext.User, out var operatorId))
        {
            var operatorAuthorization = await AuthorizeOperatorAsync(
                httpContext,
                antiforgery,
                operatorAccessService,
                requiredPermission,
                cancellationToken,
                requireAntiforgery);
            return new AdminAuthorizationOutcome(
                operatorAuthorization.OperatorId,
                operatorAuthorization.FailureResult,
                null);
        }

        var apiClientAuthorization = await AuthorizeApiClientAsync(
            httpContext,
            accessService,
            requiredPermission,
            cancellationToken);
        return new AdminAuthorizationOutcome(
            null,
            apiClientAuthorization.FailureResult,
            apiClientAuthorization.AccessResult);
    }

    private static async Task<OperatorAuthorizationOutcome> AuthorizeOperatorAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        OperatorAccessService? operatorAccessService,
        string? requiredPermission,
        CancellationToken cancellationToken,
        bool requireAntiforgery = false)
    {
        if (!TryExtractOperatorId(httpContext.User, out var operatorId))
        {
            return new OperatorAuthorizationOutcome(null, TypedResults.Unauthorized());
        }

        if (requireAntiforgery)
        {
            var csrfValidation = await ValidateAntiforgeryAsync(httpContext, antiforgery);
            if (csrfValidation is not null)
            {
                return new OperatorAuthorizationOutcome(null, csrfValidation);
            }
        }

        if (operatorAccessService is null || string.IsNullOrWhiteSpace(requiredPermission))
        {
            return new OperatorAuthorizationOutcome(operatorId, null);
        }

        var accessResult = await operatorAccessService.AuthorizeAsync(operatorId, requiredPermission, cancellationToken);
        return accessResult.Decision switch
        {
            OperatorAccessDecision.Authorized => new OperatorAuthorizationOutcome(operatorId, null),
            OperatorAccessDecision.UnknownOperator => new OperatorAuthorizationOutcome(null, TypedResults.Unauthorized()),
            OperatorAccessDecision.InactiveOperator => new OperatorAuthorizationOutcome(
                null,
                TypedResults.Problem(
                    title: "Operator inactive",
                    detail: "The authenticated operator account is inactive.",
                    statusCode: StatusCodes.Status403Forbidden)),
            OperatorAccessDecision.MissingPermission => new OperatorAuthorizationOutcome(
                null,
                TypedResults.Problem(
                    title: "Missing permission",
                    detail: $"The authenticated operator does not have the required '{requiredPermission}' permission.",
                    statusCode: StatusCodes.Status403Forbidden)),
            _ => new OperatorAuthorizationOutcome(
                null,
                TypedResults.Problem(
                    title: "Access denied",
                    detail: "The authenticated operator could not be authorized.",
                    statusCode: StatusCodes.Status403Forbidden))
        };
    }

    private static async Task<IResult?> ValidateAntiforgeryAsync(HttpContext httpContext, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return TypedResults.Problem(
                title: "Invalid antiforgery token",
                detail: "The browser request did not include a valid antiforgery token.",
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<ApiClientAuthorizationOutcome> AuthorizeApiClientAsync(
        HttpContext httpContext,
        ApiClientAccessService accessService,
        string requiredScope,
        CancellationToken cancellationToken)
    {
        var bearerToken = TryExtractBearerToken(httpContext.Request);
        if (bearerToken is null)
        {
            return new ApiClientAuthorizationOutcome(null, TypedResults.Unauthorized());
        }

        var accessResult = await accessService.AuthorizeAsync(bearerToken, requiredScope, cancellationToken);
        return accessResult.Decision switch
        {
            ApiClientAccessDecision.Authorized => new ApiClientAuthorizationOutcome(accessResult, null),
            ApiClientAccessDecision.UnknownToken => new ApiClientAuthorizationOutcome(null, TypedResults.Unauthorized()),
            ApiClientAccessDecision.InactiveClient => new ApiClientAuthorizationOutcome(
                null,
                TypedResults.Problem(
                    title: "API client inactive",
                    detail: "The supplied API client is inactive.",
                    statusCode: StatusCodes.Status403Forbidden)),
            ApiClientAccessDecision.MissingScope => new ApiClientAuthorizationOutcome(
                null,
                TypedResults.Problem(
                    title: "Missing scope",
                    detail: $"The supplied API client does not have the required '{requiredScope}' scope.",
                    statusCode: StatusCodes.Status403Forbidden)),
            _ => new ApiClientAuthorizationOutcome(
                null,
                TypedResults.Problem(
                    title: "Access denied",
                    detail: "The supplied API client could not be authorized.",
                    statusCode: StatusCodes.Status403Forbidden))
        };
    }

    private static ClaimsPrincipal CreateOperatorPrincipal(OperatorAuthenticationResult authentication)
    {
        var claims = new List<Claim>
        {
            new(OperatorCookieAuthenticationDefaults.OperatorIdClaimType, authentication.OperatorId!.Value.ToString("D")),
            new(ClaimTypes.Email, authentication.Email ?? string.Empty),
            new(ClaimTypes.Name, authentication.DisplayName ?? authentication.Email ?? string.Empty)
        };
        claims.AddRange(authentication.Permissions.Select(permission =>
            new Claim(OperatorCookieAuthenticationDefaults.PermissionClaimType, permission)));

        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            OperatorCookieAuthenticationDefaults.Scheme,
            ClaimTypes.Name,
            OperatorCookieAuthenticationDefaults.PermissionClaimType));
    }

    private static bool TryExtractOperatorId(ClaimsPrincipal principal, out Guid operatorId)
    {
        operatorId = Guid.Empty;

        if (principal.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var value = principal.FindFirstValue(OperatorCookieAuthenticationDefaults.OperatorIdClaimType);
        if (!Guid.TryParse(value, out var parsed))
        {
            return false;
        }

        operatorId = parsed;
        return true;
    }

    private static string? TryExtractBearerToken(HttpRequest request)
    {
        var authorizationHeader = request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";

        if (!authorizationHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorizationHeader[bearerPrefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private static AlertPolicy ToAlertPolicy(string code, int? digestIntervalMinutes)
    {
        var normalizedCode = code.Trim().ToLowerInvariant();
        return normalizedCode switch
        {
            "immediate" => AlertPolicy.Immediate(),
            "digest" when digestIntervalMinutes.HasValue => AlertPolicy.Digest(TimeSpan.FromMinutes(digestIntervalMinutes.Value)),
            "digest" => throw new ArgumentException("Digest alert policy requires digestIntervalMinutes.", nameof(digestIntervalMinutes)),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unsupported alert policy code.")
        };
    }

    private static SubscriptionChannel ToSubscriptionChannel(string code)
    {
        var normalizedCode = code.Trim().ToLowerInvariant();
        return normalizedCode switch
        {
            "email" => SubscriptionChannel.Email(),
            "webhook" => SubscriptionChannel.Webhook(),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unsupported subscription channel code.")
        };
    }

    private sealed record ApiClientAuthorizationOutcome(
        ApiClientAccessResult? AccessResult,
        IResult? FailureResult);

    private sealed record OperatorAuthorizationOutcome(
        Guid? OperatorId,
        IResult? FailureResult);

    private sealed record AdminAuthorizationOutcome(
        Guid? OperatorId,
        IResult? FailureResult,
        ApiClientAccessResult? AccessResult);
}
