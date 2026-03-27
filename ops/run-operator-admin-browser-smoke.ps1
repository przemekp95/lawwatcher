$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$outputRoot = Join-Path $repoRoot 'output\playwright'
$tempRoot = Join-Path $env:TEMP ('lawwatcher-operator-admin-browser-' + [Guid]::NewGuid().ToString('N'))
$runRoot = Join-Path $env:LOCALAPPDATA ('LawWatcher\artifacts-smoke\runs\operator-admin-browser-' + [Guid]::NewGuid().ToString('N'))
$stateRoot = Join-Path $runRoot 'state'
$apiOut = Join-Path $repoRoot 'api-operator-admin-browser-out.log'
$apiErr = Join-Path $repoRoot 'api-operator-admin-browser-err.log'
$portalOut = Join-Path $repoRoot 'portal-operator-admin-browser-out.log'
$portalErr = Join-Path $repoRoot 'portal-operator-admin-browser-err.log'

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
Remove-Item $apiOut, $apiErr, $portalOut, $portalErr -ErrorAction SilentlyContinue

$apiDll = Join-Path $env:LOCALAPPDATA 'LawWatcher\artifacts\bin\LawWatcher.Api\Debug\net10.0\LawWatcher.Api.dll'
$portalDll = Join-Path $env:LOCALAPPDATA 'LawWatcher\artifacts\bin\LawWatcher.Portal\Debug\net10.0\LawWatcher.Portal.dll'

if (-not (Test-Path -LiteralPath $apiDll))
{
    throw "Missing API build output at '$apiDll'. Build the host first."
}

if (-not (Test-Path -LiteralPath $portalDll))
{
    throw "Missing portal build output at '$portalDll'. Build the host first."
}

function Resolve-NpxPath
{
    $command = Get-Command npx -ErrorAction SilentlyContinue
    if ($command)
    {
        return $command.Source
    }

    $candidates = @(
        'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Microsoft\VisualStudio\NodeJs\npx.cmd',
        'C:\Program Files\nodejs\npx.cmd'
    )

    foreach ($candidate in $candidates)
    {
        if (Test-Path -LiteralPath $candidate)
        {
            return $candidate
        }
    }

    throw 'Unable to locate npx. Install Node.js or ensure npx is on PATH.'
}

function Get-FreeTcpPort
{
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try
    {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally
    {
        $listener.Stop()
    }
}

function Wait-HttpOk
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $Url,

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
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200)
            {
                return
            }
        }
        catch
        {
            if ($Process.HasExited)
            {
                $errorText = if (Test-Path -LiteralPath $ErrorLogPath) { Get-Content $ErrorLogPath -Raw } else { '' }
                throw "Host exited early for '$Url' with code $($Process.ExitCode). $errorText"
            }
        }
    }

    throw "Host did not become ready for '$Url' in time."
}

function ConvertTo-JsLiteral
{
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object] $Value
    )

    return ($Value | ConvertTo-Json -Compress -Depth 10)
}

function Invoke-PlaywrightCommand
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $NpxPath,

        [Parameter(Mandatory = $true)]
        [string] $Session,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $result = & $NpxPath --yes @playwright/cli "-s=$Session" @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $result -match '(?m)^### Error$')
    {
        throw "Playwright command failed: $($Arguments -join ' ')`n$result"
    }

    return $result
}

function Invoke-PlaywrightRunCode
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $NpxPath,

        [Parameter(Mandatory = $true)]
        [string] $Session,

        [Parameter(Mandatory = $true)]
        [string] $Code
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Code)
    $hex = -join ($bytes | ForEach-Object { $_.ToString('x2') })
    $wrappedCode = "async page => { const __source = '$hex'.match(/.{1,2}/g).map(value => String.fromCharCode(parseInt(value, 16))).join(''); const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor; const fn = new AsyncFunction('page', __source); return await fn(page); }"
    Invoke-PlaywrightCommand -NpxPath $NpxPath -Session $Session -Arguments @('run-code', $wrappedCode) | Out-Null
}

function Invoke-PlaywrightEval
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $NpxPath,

        [Parameter(Mandatory = $true)]
        [string] $Session,

        [Parameter(Mandatory = $true)]
        [string] $Expression
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Expression)
    $hex = -join ($bytes | ForEach-Object { $_.ToString('x2') })
    $wrappedExpression = "(() => { const __source = '$hex'.match(/.{1,2}/g).map(value => String.fromCharCode(parseInt(value, 16))).join(''); return () => eval(__source); })()"
    $output = Invoke-PlaywrightCommand -NpxPath $NpxPath -Session $Session -Arguments @('eval', $wrappedExpression)
    $match = [regex]::Match($output, '(?s)### Result\s*(.*?)\s*### Ran Playwright code')
    if (-not $match.Success)
    {
        throw "Unable to parse Playwright eval output.`n$output"
    }

    return ($match.Groups[1].Value.Trim() | ConvertFrom-Json)
}

function Invoke-PlaywrightWait
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $NpxPath,

        [Parameter(Mandatory = $true)]
        [string] $Session,

        [Parameter(Mandatory = $true)]
        [int] $Milliseconds
    )

    Invoke-PlaywrightRunCode -NpxPath $NpxPath -Session $Session -Code "await page.waitForTimeout($Milliseconds);"
}

function Invoke-PlaywrightScreenshot
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $NpxPath,

        [Parameter(Mandatory = $true)]
        [string] $Session,

        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $normalizedPath = $Path.Replace('\', '/')
    Invoke-PlaywrightRunCode -NpxPath $NpxPath -Session $Session -Code "await page.screenshot({ path: '$normalizedPath', fullPage: true });"
}

function Get-TokenFingerprint
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $Token
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Token)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try
    {
        $hash = $sha256.ComputeHash($bytes)
        return 'sha256:' + ([System.BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant())
    }
    finally
    {
        $sha256.Dispose()
    }
}

function Test-PlaywrightChromiumInstalled
{
    $playwrightCacheRoot = Join-Path $env:LOCALAPPDATA 'ms-playwright'
    if (-not (Test-Path -LiteralPath $playwrightCacheRoot))
    {
        return $false
    }

    return [bool](Get-ChildItem -Path $playwrightCacheRoot -Directory -Filter 'chromium-*' -ErrorAction SilentlyContinue)
}

$npxPath = Resolve-NpxPath
$nodeDirectory = Split-Path -Parent $npxPath
$env:PATH = "$nodeDirectory;$env:PATH"

if (-not (Test-PlaywrightChromiumInstalled))
{
    & $npxPath --yes @playwright/cli install-browser | Out-Null
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Playwright browser install failed.'
    }
}

$session = 'lawwatcher-admin-browser-' + [Guid]::NewGuid().ToString('N')
$apiPort = Get-FreeTcpPort
$portalPort = Get-FreeTcpPort
$apiBaseUrl = "http://127.0.0.1:$apiPort"
$portalBaseUrl = "http://127.0.0.1:$portalPort"

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.nuget\packages'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:LawWatcher__Storage__Provider = 'files'
$env:LawWatcher__Storage__StateRoot = $stateRoot
$env:LawWatcher__SeedData__EnableWebhookSubscriptionSeed = 'false'
$env:ASPNETCORE_URLS = $apiBaseUrl

$apiProcess = Start-Process -FilePath 'dotnet' -ArgumentList @($apiDll) -WorkingDirectory $repoRoot -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -WindowStyle Hidden -PassThru
$portalProcess = $null

try
{
    Wait-HttpOk -Url "$apiBaseUrl/v1/system/capabilities" -Process $apiProcess -ErrorLogPath $apiErr

    $env:LawWatcher__PortalApi__BaseUrl = $apiBaseUrl
    $env:ASPNETCORE_URLS = $portalBaseUrl
    $portalProcess = Start-Process -FilePath 'dotnet' -ArgumentList @($portalDll) -WorkingDirectory $repoRoot -RedirectStandardOutput $portalOut -RedirectStandardError $portalErr -WindowStyle Hidden -PassThru
    Wait-HttpOk -Url "$portalBaseUrl/admin" -Process $portalProcess -ErrorLogPath $portalErr

    Invoke-PlaywrightCommand -NpxPath $npxPath -Session $session -Arguments @('open', "$portalBaseUrl/admin") | Out-Null
    Invoke-PlaywrightCommand -NpxPath $npxPath -Session $session -Arguments @('resize', '1440', '2400') | Out-Null
    Invoke-PlaywrightWait -NpxPath $npxPath -Session $session -Milliseconds 2500

    $operatorEmail = "browser.ops.$([Guid]::NewGuid().ToString('N').Substring(0, 8))@lawwatcher.local"
    $operatorDisplayName = 'Browser Smoke Operator'
    $updatedOperatorDisplayName = 'Browser Smoke Operator Updated'
    $operatorPassword = 'BrowserSmoke123!'
    $profileName = "Browser Smoke Profile $([Guid]::NewGuid().ToString('N').Substring(0, 6))"
    $subscriptionEmail = "browser.subscription.$([Guid]::NewGuid().ToString('N').Substring(0, 8))@example.test"
    $webhookName = "Browser Smoke Webhook $([Guid]::NewGuid().ToString('N').Substring(0, 6))"
    $updatedWebhookName = "$webhookName Updated"
    $webhookCallbackUrl = "https://hooks.example.test/$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $updatedWebhookCallbackUrl = "$webhookCallbackUrl/v2"
    $apiClientName = "Browser Smoke API Client $([Guid]::NewGuid().ToString('N').Substring(0, 6))"
    $updatedApiClientName = "$apiClientName Updated"
    $apiClientIdentifier = "browser-client-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $apiClientToken = "browser-client-token-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $rotatedApiClientToken = "browser-client-token-rotated-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $rotatedFingerprint = Get-TokenFingerprint -Token $rotatedApiClientToken

    $operatorEmailJs = ConvertTo-JsLiteral $operatorEmail
    $operatorDisplayNameJs = ConvertTo-JsLiteral $operatorDisplayName
    $updatedOperatorDisplayNameJs = ConvertTo-JsLiteral $updatedOperatorDisplayName
    $operatorPasswordJs = ConvertTo-JsLiteral $operatorPassword
    $profileNameJs = ConvertTo-JsLiteral $profileName
    $subscriptionEmailJs = ConvertTo-JsLiteral $subscriptionEmail
    $webhookNameJs = ConvertTo-JsLiteral $webhookName
    $updatedWebhookNameJs = ConvertTo-JsLiteral $updatedWebhookName
    $webhookCallbackUrlJs = ConvertTo-JsLiteral $webhookCallbackUrl
    $updatedWebhookCallbackUrlJs = ConvertTo-JsLiteral $updatedWebhookCallbackUrl
    $apiClientNameJs = ConvertTo-JsLiteral $apiClientName
    $updatedApiClientNameJs = ConvertTo-JsLiteral $updatedApiClientName
    $apiClientIdentifierJs = ConvertTo-JsLiteral $apiClientIdentifier
    $apiClientTokenJs = ConvertTo-JsLiteral $apiClientToken
    $rotatedApiClientTokenJs = ConvertTo-JsLiteral $rotatedApiClientToken
    $rotatedFingerprintJs = ConvertTo-JsLiteral $rotatedFingerprint

    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.waitForFunction(() => {
  const email = document.querySelector('#operator-email');
  const password = document.querySelector('#operator-password');
  return !!email && !!password && document.body.innerText.includes('Operator access') && document.body.innerText.includes('Sign in');
});
"@
    $loginFormVisible = $true

    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.locator('#operator-email').fill('admin@lawwatcher.local');
await page.locator('#operator-password').fill('Admin123!');
await page.getByRole('button', { name: 'Sign in' }).click();
await page.waitForFunction(() => document.body.innerText.includes('Operator session established.') && document.body.innerText.includes('Create profile'));
"@

    $authenticatedAfterLogin = $true

    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.locator('#operator-create-email').fill($operatorEmailJs);
await page.locator('#operator-create-display').fill($operatorDisplayNameJs);
await page.locator('#operator-create-password').fill($operatorPasswordJs);
await page.locator('#operator-create-permissions').fill('profiles:write, subscriptions:write, webhooks:write, api-clients:write');
await page.getByRole('button', { name: 'Create operator' }).click();
await page.waitForFunction(() => {
  const hasOperator = document.body.innerText.includes($operatorEmailJs) && document.body.innerText.includes($operatorDisplayNameJs);
  const options = Array.from(document.querySelectorAll('#operator-update-target option'));
  return hasOperator && options.some(option => option.textContent?.trim() === $operatorEmailJs);
});
"@

    $createdOperatorVisible = $true

    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.locator('#operator-update-target').selectOption({ label: $operatorEmailJs });
await page.locator('#operator-update-display').fill($updatedOperatorDisplayNameJs);
await page.locator('#operator-update-permissions').fill('profiles:write, subscriptions:write, webhooks:write, api-clients:write');
await page.getByRole('button', { name: 'Update operator' }).click();
await page.waitForFunction(() => document.body.innerText.includes($updatedOperatorDisplayNameJs));
"@

    $updatedOperatorVisible = $true

    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.locator('article.admin-item-card').filter({ hasText: $updatedOperatorDisplayNameJs }).getByRole('button', { name: 'Deactivate' }).click();
await page.waitForFunction(() => {
  const cards = Array.from(document.querySelectorAll('article.admin-item-card'));
  const card = cards.find(candidate => candidate.innerText.includes($updatedOperatorDisplayNameJs));
  return !!card && card.innerText.includes('inactive');
});
"@

    $deactivatedOperatorMarkedInactive = $true

    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.locator('#profile-name').fill($profileNameJs);
await page.locator('#profile-keywords').fill('browser, smoke');
await page.getByRole('button', { name: 'Create profile' }).click();
await page.waitForFunction(() => {
  const hasProfile = document.body.innerText.includes($profileNameJs);
  const options = Array.from(document.querySelectorAll('#subscription-profile option'));
  return hasProfile && options.some(option => option.textContent?.trim() === $profileNameJs);
});
"@

    $createdProfileVisible = $true

    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.locator('#subscription-profile').selectOption({ label: $profileNameJs });
await page.locator('#subscription-subscriber').fill($subscriptionEmailJs);
await page.getByRole('button', { name: 'Create subscription' }).click();
await page.waitForFunction(() => document.body.innerText.includes($subscriptionEmailJs));
"@

    $createdSubscriptionVisible = $true

    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.locator('#webhook-name').fill($webhookNameJs);
await page.locator('#webhook-url').fill($webhookCallbackUrlJs);
await page.locator('#webhook-events').fill('alert.created');
await page.getByRole('button', { name: 'Create webhook' }).click();
await page.waitForFunction(() => {
  const hasWebhook = document.body.innerText.includes($webhookNameJs) && document.body.innerText.includes($webhookCallbackUrlJs);
  const options = Array.from(document.querySelectorAll('#webhook-update-target option'));
  return hasWebhook && options.some(option => option.textContent?.trim() === $webhookNameJs);
});
"@

    $createdWebhookVisible = $true

    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.locator('#webhook-update-target').selectOption({ label: $webhookNameJs });
await page.locator('#webhook-update-name').fill($updatedWebhookNameJs);
await page.locator('#webhook-update-url').fill($updatedWebhookCallbackUrlJs);
await page.locator('#webhook-update-events').fill('alert.created, bill.imported');
await page.getByRole('button', { name: 'Update webhook' }).click();
await page.waitForFunction(() => document.body.innerText.includes($updatedWebhookNameJs) && document.body.innerText.includes($updatedWebhookCallbackUrlJs));
"@

    $updatedWebhookVisible = $true

    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.locator('#api-client-name').fill($apiClientNameJs);
await page.locator('#api-client-identifier').fill($apiClientIdentifierJs);
await page.locator('#api-client-token').fill($apiClientTokenJs);
await page.locator('#api-client-scopes').fill('replays:write, webhooks:write');
await page.getByRole('button', { name: 'Create API client' }).click();
await page.waitForFunction(() => {
  const hasApiClient = document.body.innerText.includes($apiClientIdentifierJs) && document.body.innerText.includes($apiClientNameJs);
  const options = Array.from(document.querySelectorAll('#api-client-update-target option'));
  return hasApiClient && options.some(option => option.textContent?.trim() === $apiClientIdentifierJs);
});
"@

    $createdApiClientVisible = $true

    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.locator('#api-client-update-target').selectOption({ label: $apiClientIdentifierJs });
await page.locator('#api-client-update-name').fill($updatedApiClientNameJs);
await page.locator('#api-client-update-scopes').fill('replays:write, api-clients:write');
await page.locator('#api-client-update-token').fill($rotatedApiClientTokenJs);
await page.getByRole('button', { name: 'Update API client' }).click();
await page.waitForFunction(() => document.body.innerText.includes($updatedApiClientNameJs) && document.body.innerText.includes($rotatedFingerprintJs));
"@

    $updatedApiClientVisible = $true

    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.locator('article.admin-item-card').filter({ hasText: $subscriptionEmailJs }).getByRole('button', { name: 'Deactivate' }).click();
await page.waitForFunction(() => !document.body.innerText.includes($subscriptionEmailJs));
await page.locator('article.admin-item-card').filter({ hasText: $profileNameJs }).getByRole('button', { name: 'Deactivate' }).click();
await page.waitForFunction(() => !document.body.innerText.includes($profileNameJs));
await page.locator('article.admin-item-card').filter({ hasText: $updatedWebhookNameJs }).getByRole('button', { name: 'Deactivate' }).click();
await page.waitForFunction(() => {
  const cards = Array.from(document.querySelectorAll('article.admin-item-card'));
  const card = cards.find(candidate => candidate.innerText.includes($updatedWebhookNameJs));
  return !!card && card.innerText.includes('inactive');
});
await page.locator('article.admin-item-card').filter({ hasText: $updatedApiClientNameJs }).getByRole('button', { name: 'Deactivate' }).click();
await page.waitForFunction(() => {
  const cards = Array.from(document.querySelectorAll('article.admin-item-card'));
  const card = cards.find(candidate => candidate.innerText.includes($updatedApiClientNameJs));
  return !!card && card.innerText.includes('inactive');
});
"@

    $subscriptionRemoved = $true
    $profileRemoved = $true
    $webhookMarkedInactive = $true
    $apiClientMarkedInactive = $true

    $authenticatedScreenshot = Join-Path $outputRoot 'admin-authenticated.png'
    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.screenshot({ path: $((ConvertTo-JsLiteral $authenticatedScreenshot).Trim()), fullPage: true });
"@

    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.getByRole('button', { name: 'Sign out' }).click();
await page.waitForFunction(() => document.body.innerText.includes('Sign in') && document.body.innerText.includes('Operator access'));
"@

    $loggedOutSignInVisible = $true

    $loggedOutScreenshot = Join-Path $outputRoot 'admin-logged-out.png'
    Invoke-PlaywrightRunCode -NpxPath $npxPath -Session $session -Code @"
await page.screenshot({ path: $((ConvertTo-JsLiteral $loggedOutScreenshot).Trim()), fullPage: true });
"@

    $summary = [ordered]@{
        verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        apiBaseUrl = $apiBaseUrl
        portalBaseUrl = $portalBaseUrl
        loginFormVisible = [bool]$loginFormVisible
        authenticatedAfterLogin = [bool]$authenticatedAfterLogin
        createdOperatorVisible = [bool]$createdOperatorVisible
        updatedOperatorVisible = [bool]$updatedOperatorVisible
        deactivatedOperatorMarkedInactive = [bool]$deactivatedOperatorMarkedInactive
        createdProfileVisible = [bool]$createdProfileVisible
        createdSubscriptionVisible = [bool]$createdSubscriptionVisible
        createdWebhookVisible = [bool]$createdWebhookVisible
        updatedWebhookVisible = [bool]$updatedWebhookVisible
        createdApiClientVisible = [bool]$createdApiClientVisible
        updatedApiClientVisible = [bool]$updatedApiClientVisible
        subscriptionRemovedAfterDeactivate = [bool]$subscriptionRemoved
        profileRemovedAfterDeactivate = [bool]$profileRemoved
        webhookMarkedInactiveAfterDeactivate = [bool]$webhookMarkedInactive
        apiClientMarkedInactiveAfterDeactivate = [bool]$apiClientMarkedInactive
        loggedOutSignInVisible = [bool]$loggedOutSignInVisible
        operatorEmail = $operatorEmail
        profileName = $profileName
        subscriptionEmail = $subscriptionEmail
        webhookName = $updatedWebhookName
        apiClientIdentifier = $apiClientIdentifier
        rotatedApiClientFingerprint = $rotatedFingerprint
        screenshots = @(
            [ordered]@{ name = 'authenticated'; path = $authenticatedScreenshot },
            [ordered]@{ name = 'loggedOut'; path = $loggedOutScreenshot }
        )
        stateRoot = $stateRoot
        apiStdoutLog = $apiOut
        apiStderrLog = $apiErr
        portalStdoutLog = $portalOut
        portalStderrLog = $portalErr
    }

    $requiredFlags = @(
        'loginFormVisible',
        'authenticatedAfterLogin',
        'createdOperatorVisible',
        'updatedOperatorVisible',
        'deactivatedOperatorMarkedInactive',
        'createdProfileVisible',
        'createdSubscriptionVisible',
        'createdWebhookVisible',
        'updatedWebhookVisible',
        'createdApiClientVisible',
        'updatedApiClientVisible',
        'subscriptionRemovedAfterDeactivate',
        'profileRemovedAfterDeactivate',
        'webhookMarkedInactiveAfterDeactivate',
        'apiClientMarkedInactiveAfterDeactivate',
        'loggedOutSignInVisible'
    )
    $failedFlags = @(
        foreach ($flagName in $requiredFlags)
        {
            if (-not [bool]$summary[$flagName])
            {
                $flagName
            }
        }
    )
    $missingScreenshots = @(
        foreach ($screenshot in $summary.screenshots)
        {
            if (-not (Test-Path -LiteralPath $screenshot.path))
            {
                $screenshot.path
            }
        }
    )

    $summaryPath = Join-Path $outputRoot 'operator-admin-browser-summary.json'
    $summary | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryPath
    if ($failedFlags.Count -gt 0 -or $missingScreenshots.Count -gt 0)
    {
        $details = [ordered]@{
            failedFlags = $failedFlags
            missingScreenshots = $missingScreenshots
            summaryPath = $summaryPath
        } | ConvertTo-Json -Depth 6
        throw "Operator admin browser smoke failed.`n$details"
    }

    $summary | ConvertTo-Json -Depth 6
}
finally
{
    try
    {
        Invoke-PlaywrightCommand -NpxPath $npxPath -Session $session -Arguments @('close') | Out-Null
    }
    catch
    {
    }

    try
    {
        Invoke-PlaywrightCommand -NpxPath $npxPath -Session $session -Arguments @('delete-data') | Out-Null
    }
    catch
    {
    }

    if ($portalProcess -and -not $portalProcess.HasExited)
    {
        Stop-Process -Id $portalProcess.Id -Force
    }

    if ($apiProcess -and -not $apiProcess.HasExited)
    {
        Stop-Process -Id $apiProcess.Id -Force
    }
}
