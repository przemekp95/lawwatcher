$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.nuget\packages'
$env:ASPNETCORE_ENVIRONMENT = 'Development'

Add-Type -AssemblyName System.Net.Http

$apiDll = Join-Path $env:LOCALAPPDATA 'LawWatcher\artifacts\bin\LawWatcher.Api\Debug\net10.0\LawWatcher.Api.dll'
if (-not (Test-Path -LiteralPath $apiDll))
{
    throw "Missing API build output at '$apiDll'. Build the host first."
}

function Send-HttpRequest
{
    param(
        [Parameter(Mandatory = $true)]
        [System.Net.Http.HttpClient] $Client,

        [Parameter(Mandatory = $true)]
        [string] $Method,

        [Parameter(Mandatory = $true)]
        [string] $Path,

        [object] $Body,

        [hashtable] $Headers
    )

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Path)
    try
    {
        if ($Headers)
        {
            foreach ($header in $Headers.GetEnumerator())
            {
                $null = $request.Headers.TryAddWithoutValidation($header.Key, [string]$header.Value)
            }
        }

        if ($PSBoundParameters.ContainsKey('Body'))
        {
            $json = $Body | ConvertTo-Json -Depth 8 -Compress
            $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
        }

        $response = $Client.SendAsync($request).GetAwaiter().GetResult()
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Content = $content
            Json = if ([string]::IsNullOrWhiteSpace($content)) { $null } else { $content | ConvertFrom-Json }
        }
    }
    finally
    {
        $request.Dispose()
    }
}

function Get-Ready
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $BaseUrl,

        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process] $Process,

        [Parameter(Mandatory = $true)]
        [string] $ErrorLogPath
    )

    for ($attempt = 0; $attempt -lt 60; $attempt++)
    {
        Start-Sleep -Milliseconds 500

        try
        {
            $null = Invoke-WebRequest -Uri "$BaseUrl/v1/system/capabilities" -UseBasicParsing -TimeoutSec 5
            return
        }
        catch
        {
            if ($Process.HasExited)
            {
                $errorText = if (Test-Path -LiteralPath $ErrorLogPath) { Get-Content $ErrorLogPath -Raw } else { '' }
                throw "API exited early with code $($Process.ExitCode). $errorText"
            }
        }
    }

    throw "API did not become ready in time."
}

$apiPort = 5304
$runRoot = Join-Path $env:LOCALAPPDATA ('LawWatcher\artifacts-smoke\runs\operator-admin-' + [Guid]::NewGuid().ToString('N'))
$stateRoot = Join-Path $runRoot 'state'
$apiOut = Join-Path $repoRoot 'api-operator-admin-smoke-out.log'
$apiErr = Join-Path $repoRoot 'api-operator-admin-smoke-err.log'

New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
Remove-Item $apiOut, $apiErr -ErrorAction SilentlyContinue

$env:LawWatcher__Storage__Provider = 'files'
$env:LawWatcher__Storage__StateRoot = $stateRoot
$env:LawWatcher__SeedData__EnableWebhookSubscriptionSeed = 'false'
$env:ASPNETCORE_URLS = "http://127.0.0.1:$apiPort"
$apiProcess = Start-Process -FilePath 'dotnet' -ArgumentList @($apiDll) -WorkingDirectory $repoRoot -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -WindowStyle Hidden -PassThru

try
{
    $baseUrl = "http://127.0.0.1:$apiPort"
    Get-Ready -BaseUrl $baseUrl -Process $apiProcess -ErrorLogPath $apiErr

    $anonHandler = [System.Net.Http.HttpClientHandler]::new()
    $anonHandler.CookieContainer = [System.Net.CookieContainer]::new()
    $anonClient = [System.Net.Http.HttpClient]::new($anonHandler)
    $anonClient.BaseAddress = [Uri]$baseUrl

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.CookieContainer = [System.Net.CookieContainer]::new()
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.BaseAddress = [Uri]$baseUrl

    $unauthorizedOperators = Send-HttpRequest -Client $anonClient -Method 'GET' -Path '/v1/operators'
    $initialSession = Send-HttpRequest -Client $client -Method 'GET' -Path '/v1/operator/session'
    $initialCsrf = [string]$initialSession.Json.csrfRequestToken

    $loginWithoutCsrf = Send-HttpRequest -Client $client -Method 'POST' -Path '/v1/operator/login' -Body @{
        email = 'admin@lawwatcher.local'
        password = 'Admin123!'
    }

    $login = Send-HttpRequest -Client $client -Method 'POST' -Path '/v1/operator/login' -Headers @{
        'X-LawWatcher-CSRF' = $initialCsrf
    } -Body @{
        email = 'admin@lawwatcher.local'
        password = 'Admin123!'
    }

    $sessionAfterLogin = Send-HttpRequest -Client $client -Method 'GET' -Path '/v1/operator/session'
    $csrf = [string]$sessionAfterLogin.Json.csrfRequestToken
    $me = Send-HttpRequest -Client $client -Method 'GET' -Path '/v1/operator/me'

    $createOperator = Send-HttpRequest -Client $client -Method 'POST' -Path '/v1/operators' -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    } -Body @{
        email = 'ops.tester@lawwatcher.local'
        displayName = 'Ops Tester'
        password = 'OpsTester123!'
        permissions = @('profiles:write', 'subscriptions:write')
    }

    $operatorId = [string]$createOperator.Json.id
    $updateOperator = Send-HttpRequest -Client $client -Method 'PATCH' -Path "/v1/operators/$operatorId" -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    } -Body @{
        displayName = 'Ops Tester Updated'
        permissions = @('profiles:write', 'subscriptions:write', 'webhooks:write')
    }

    $operators = Send-HttpRequest -Client $client -Method 'GET' -Path '/v1/operators'
    $profileWithoutCsrf = Send-HttpRequest -Client $client -Method 'POST' -Path '/v1/profiles' -Body @{
        name = 'No CSRF'
        alertPolicy = 'immediate'
        digestIntervalMinutes = $null
        keywords = @('vat')
    }

    $createProfile = Send-HttpRequest -Client $client -Method 'POST' -Path '/v1/profiles' -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    } -Body @{
        name = 'Operator Created Profile'
        alertPolicy = 'immediate'
        digestIntervalMinutes = $null
        keywords = @('vat', 'cit')
    }

    $profileId = [string]$createProfile.Json.id
    $addProfileRule = Send-HttpRequest -Client $client -Method 'POST' -Path "/v1/profiles/$profileId/rules" -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    } -Body @{
        keyword = 'akcyza'
    }

    $changeProfilePolicy = Send-HttpRequest -Client $client -Method 'PATCH' -Path "/v1/profiles/$profileId/alert-policy" -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    } -Body @{
        alertPolicy = 'digest'
        digestIntervalMinutes = 180
    }

    $profiles = Send-HttpRequest -Client $client -Method 'GET' -Path '/v1/profiles'
    $createSubscription = Send-HttpRequest -Client $client -Method 'POST' -Path '/v1/subscriptions' -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    } -Body @{
        profileId = $profileId
        subscriber = 'ops.tester@lawwatcher.local'
        channel = 'email'
        alertPolicy = 'immediate'
        digestIntervalMinutes = $null
    }

    $subscriptionId = [string]$createSubscription.Json.id
    $changeSubscriptionPolicy = Send-HttpRequest -Client $client -Method 'PATCH' -Path "/v1/subscriptions/$subscriptionId/alert-policy" -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    } -Body @{
        alertPolicy = 'digest'
        digestIntervalMinutes = 240
    }

    $subscriptions = Send-HttpRequest -Client $client -Method 'GET' -Path '/v1/subscriptions'
    $deactivateSubscription = Send-HttpRequest -Client $client -Method 'DELETE' -Path "/v1/subscriptions/$subscriptionId" -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    }
    $subscriptionsAfterDeactivate = Send-HttpRequest -Client $client -Method 'GET' -Path '/v1/subscriptions'
    $deactivateProfile = Send-HttpRequest -Client $client -Method 'DELETE' -Path "/v1/profiles/$profileId" -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    }
    $profilesAfterDeactivate = Send-HttpRequest -Client $client -Method 'GET' -Path '/v1/profiles'

    $createWebhook = Send-HttpRequest -Client $client -Method 'POST' -Path '/v1/webhooks' -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    } -Body @{
        name = 'Operator Managed Webhook'
        callbackUrl = 'https://hooks.example.test/operator-admin'
        eventTypes = @('alert.created')
    }

    $webhookId = [string]$createWebhook.Json.id
    $updateWebhook = Send-HttpRequest -Client $client -Method 'PATCH' -Path "/v1/webhooks/$webhookId" -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    } -Body @{
        name = 'Operator Managed Webhook Updated'
        callbackUrl = 'https://hooks.example.test/operator-admin/v2'
        eventTypes = @('alert.created', 'bill.imported')
    }

    $webhooksAfterUpdate = Send-HttpRequest -Client $client -Method 'GET' -Path '/v1/webhooks'
    $deactivateWebhook = Send-HttpRequest -Client $client -Method 'DELETE' -Path "/v1/webhooks/$webhookId" -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    }

    $createApiClient = Send-HttpRequest -Client $client -Method 'POST' -Path '/v1/api-clients' -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    } -Body @{
        name = 'Operator Managed API Client'
        clientIdentifier = 'operator-managed-client'
        token = 'operator-managed-token'
        scopes = @('replays:write', 'webhooks:write')
    }

    $apiClientId = [string]$createApiClient.Json.id
    $updateApiClient = Send-HttpRequest -Client $client -Method 'PATCH' -Path "/v1/api-clients/$apiClientId" -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    } -Body @{
        name = 'Operator Managed API Client Updated'
        token = 'operator-managed-token-rotated'
        scopes = @('replays:write', 'api-clients:write')
    }

    $replayWithRotatedToken = Send-HttpRequest -Client $anonClient -Method 'POST' -Path '/v1/replays' -Headers @{
        Authorization = 'Bearer operator-managed-token-rotated'
    } -Body @{
        scope = 'sql-projections'
    }
    $replayWithStaleToken = Send-HttpRequest -Client $anonClient -Method 'POST' -Path '/v1/replays' -Headers @{
        Authorization = 'Bearer operator-managed-token'
    } -Body @{
        scope = 'sql-projections'
    }

    $apiClients = Send-HttpRequest -Client $client -Method 'GET' -Path '/v1/api-clients'
    $deactivateApiClient = Send-HttpRequest -Client $client -Method 'DELETE' -Path "/v1/api-clients/$apiClientId" -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    }
    $logout = Send-HttpRequest -Client $client -Method 'POST' -Path '/v1/operator/logout' -Headers @{
        'X-LawWatcher-CSRF' = $csrf
    }
    $meAfterLogout = Send-HttpRequest -Client $client -Method 'GET' -Path '/v1/operator/me'

    $updatedWebhookReadModel = @($webhooksAfterUpdate.Json | Where-Object { $_.id -eq $webhookId })[0]
    $updatedApiClientReadModel = @($apiClients.Json | Where-Object { $_.id -eq $apiClientId })[0]

    [ordered]@{
        unauthorizedOperatorsStatus = $unauthorizedOperators.StatusCode
        loginWithoutCsrfStatus = $loginWithoutCsrf.StatusCode
        authenticatedAfterLogin = [bool]$sessionAfterLogin.Json.isAuthenticated
        authenticatedEmail = [string]$me.Json.email
        operatorCount = @($operators.Json).Count
        createdOperatorId = $operatorId
        updateOperatorStatus = [string]$updateOperator.Json.status
        profileWithoutCsrfStatus = $profileWithoutCsrf.StatusCode
        profileCount = @($profiles.Json).Count
        createdProfileId = $profileId
        profileStatuses = @([string]$createProfile.Json.status, [string]$addProfileRule.Json.status, [string]$changeProfilePolicy.Json.status)
        deactivatedProfileStatus = [string]$deactivateProfile.Json.status
        subscriptionCount = @($subscriptions.Json).Count
        createdSubscriptionId = $subscriptionId
        subscriptionStatuses = @([string]$createSubscription.Json.status, [string]$changeSubscriptionPolicy.Json.status)
        deactivatedSubscriptionStatus = [string]$deactivateSubscription.Json.status
        subscriptionCountAfterDeactivate = @($subscriptionsAfterDeactivate.Json).Count
        profileCountAfterDeactivate = @($profilesAfterDeactivate.Json).Count
        webhookCount = @($webhooksAfterUpdate.Json).Count
        createdWebhookId = $webhookId
        webhookStatuses = @([string]$createWebhook.Json.status, [string]$updateWebhook.Json.status)
        updatedWebhookName = [string]$updatedWebhookReadModel.name
        updatedWebhookCallbackUrl = [string]$updatedWebhookReadModel.callbackUrl
        updatedWebhookEventTypeCount = @($updatedWebhookReadModel.eventTypes).Count
        deactivatedWebhookStatus = [string]$deactivateWebhook.Json.status
        apiClientCount = @($apiClients.Json).Count
        createdApiClientId = $apiClientId
        apiClientStatuses = @([string]$createApiClient.Json.status, [string]$updateApiClient.Json.status)
        updatedApiClientName = [string]$updatedApiClientReadModel.name
        updatedApiClientFingerprint = [string]$updatedApiClientReadModel.tokenFingerprint
        updatedApiClientScopeCount = @($updatedApiClientReadModel.scopes).Count
        replayWithRotatedTokenStatus = $replayWithRotatedToken.StatusCode
        replayWithStaleTokenStatus = $replayWithStaleToken.StatusCode
        deactivatedApiClientStatus = [string]$deactivateApiClient.Json.status
        logoutAuthenticated = [bool]$logout.Json.isAuthenticated
        meAfterLogoutStatus = $meAfterLogout.StatusCode
        stateRoot = $stateRoot
        stdoutLog = $apiOut
        stderrLog = $apiErr
    } | ConvertTo-Json -Depth 6
}
finally
{
    foreach ($resource in @($client, $handler, $anonClient, $anonHandler))
    {
        if ($null -ne $resource)
        {
            $resource.Dispose()
        }
    }

    if ($null -ne $apiProcess -and -not $apiProcess.HasExited)
    {
        Stop-Process -Id $apiProcess.Id -Force
        $apiProcess.WaitForExit()
    }
}
