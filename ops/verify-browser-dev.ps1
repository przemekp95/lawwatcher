$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$portalBaseUrl = if ($env:LawWatcherPortalBaseUrl) { $env:LawWatcherPortalBaseUrl } else { 'http://127.0.0.1:5256' }
$apiBaseUrl = if ($env:LawWatcherApiBaseUrl) { $env:LawWatcherApiBaseUrl } else { 'http://127.0.0.1:5290' }
$outputRoot = Join-Path $repoRoot 'output\playwright'
$tempRoot = Join-Path $env:TEMP 'lawwatcher-playwright'

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

function Assert-HttpOk([string]$url)
{
    try
    {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10
        if ($response.StatusCode -ne 200)
        {
            throw "Unexpected status $($response.StatusCode) for '$url'."
        }

        return $response
    }
    catch
    {
        throw "Failed to reach '$url'. Start the local hosts first. $($_.Exception.Message)"
    }
}

function Invoke-PlaywrightScreenshot(
    [string]$npxPath,
    [string]$url,
    [string]$selector,
    [string]$tempFile)
{
    $arguments = @(
        '--yes',
        'playwright',
        'screenshot',
        '--browser', 'chromium',
        '--viewport-size', '1440,1200',
        '--full-page',
        '--wait-for-timeout', '2500'
    )

    if (-not [string]::IsNullOrWhiteSpace($selector))
    {
        $arguments += @('--wait-for-selector', $selector)
    }

    $arguments += @($url, $tempFile)

    & $npxPath @arguments | Out-Null
    if ($LASTEXITCODE -ne 0)
    {
        throw "Playwright screenshot failed for '$url'."
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
$npxDirectory = Split-Path -Parent $npxPath
$env:PATH = "$npxDirectory;$env:PATH"

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

$portalResponse = Assert-HttpOk "$portalBaseUrl/"
$apiResponse = Assert-HttpOk "$apiBaseUrl/v1/system/capabilities"
$adminResponse = Assert-HttpOk "$portalBaseUrl/admin"

if (-not (Test-PlaywrightChromiumInstalled))
{
    & $npxPath --yes playwright install chromium | Out-Null
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Playwright browser install failed.'
    }
}

$screens = @(
    @{
        Name = 'home'
        Url = "$portalBaseUrl/"
        Selector = 'text=Legislative monitoring dashboard'
    },
    @{
        Name = 'search'
        Url = "$portalBaseUrl/search?q=VAT"
        Selector = ''
    },
    @{
        Name = 'activity'
        Url = "$portalBaseUrl/activity"
        Selector = 'text=Alerts and event feed'
    },
    @{
        Name = 'admin'
        Url = "$portalBaseUrl/admin"
        Selector = 'text=Operator access'
    }
)

$artifacts = foreach ($screen in $screens)
{
    $tempFile = Join-Path $tempRoot "$($screen.Name).png"
    $targetFile = Join-Path $outputRoot "$($screen.Name).png"

    Invoke-PlaywrightScreenshot -npxPath $npxPath -url $screen.Url -selector $screen.Selector -tempFile $tempFile
    Move-Item -Force -LiteralPath $tempFile -Destination $targetFile

    $fileInfo = Get-Item -LiteralPath $targetFile
    [pscustomobject]@{
        name = $screen.Name
        url = $screen.Url
        path = $targetFile
        bytes = $fileInfo.Length
    }
}

$searchHtml = Assert-HttpOk "$portalBaseUrl/search?q=VAT"
$searchContainsHitLabel = $searchHtml.Content -like '*hit(s) for*'
$searchContainsVatTitle = $searchHtml.Content -like '*Ustawa o zmianie VAT*'
$adminContainsOperatorAccess = $adminResponse.Content -like '*Operator access*'
$adminContainsSignIn = $adminResponse.Content -like '*Sign in*'

$summary = [pscustomobject]@{
    verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    portalStatusCode = $portalResponse.StatusCode
    apiStatusCode = $apiResponse.StatusCode
    searchContainsHitLabel = $searchContainsHitLabel
    searchContainsVatTitle = $searchContainsVatTitle
    adminContainsOperatorAccess = $adminContainsOperatorAccess
    adminContainsSignIn = $adminContainsSignIn
    screenshots = $artifacts
}

$summaryPath = Join-Path $outputRoot 'browser-summary.json'
$summary | ConvertTo-Json -Depth 5 | Set-Content -Path $summaryPath
$summary | ConvertTo-Json -Depth 5
