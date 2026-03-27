$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.nuget\packages'
$env:ASPNETCORE_ENVIRONMENT = 'Development'

function Assert-OllamaAvailable
{
    param([string]$BaseUrl = 'http://127.0.0.1:11434')

    try
    {
        $response = Invoke-RestMethod -Uri "$BaseUrl/api/tags" -TimeoutSec 5
        if ($null -eq $response)
        {
            throw 'Empty response.'
        }
    }
    catch
    {
        throw "Ollama runtime is not reachable at '$BaseUrl'. Start the supported Docker AI profile first."
    }
}

Assert-OllamaAvailable

function Resolve-PreferredHostRuntime(
    [string]$publishedPath,
    [string]$buildPath,
    [string]$label)
{
    $publishedExists = Test-Path -LiteralPath $publishedPath
    $buildExists = Test-Path -LiteralPath $buildPath

    if (-not $publishedExists -and -not $buildExists)
    {
        throw "Missing $label build output at '$buildPath' and publish output at '$publishedPath'. Build or publish the host first."
    }

    if ($publishedExists -and $buildExists)
    {
        $publishedItem = Get-Item -LiteralPath $publishedPath
        $buildItem = Get-Item -LiteralPath $buildPath
        if ($publishedItem.LastWriteTimeUtc -ge $buildItem.LastWriteTimeUtc)
        {
            return [pscustomobject]@{
                Kind = 'exe'
                Path = $publishedPath
            }
        }

        return [pscustomobject]@{
            Kind = 'dll'
            Path = $buildPath
        }
    }

    if ($publishedExists)
    {
        return [pscustomobject]@{
            Kind = 'exe'
            Path = $publishedPath
        }
    }

    return [pscustomobject]@{
        Kind = 'dll'
        Path = $buildPath
    }
}

function Start-HostProcess(
    [pscustomobject]$runtime,
    [string]$workingDirectory,
    [string]$stdoutPath,
    [string]$stderrPath)
{
    if ($runtime.Kind -eq 'exe')
    {
        return Start-Process -FilePath $runtime.Path -WorkingDirectory $workingDirectory -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -WindowStyle Hidden -PassThru
    }

    return Start-Process -FilePath 'dotnet' -ArgumentList @($runtime.Path) -WorkingDirectory $workingDirectory -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -WindowStyle Hidden -PassThru
}

function Get-Json(
    [string]$url)
{
    $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5
    return [pscustomobject]@{
        StatusCode = [int]$response.StatusCode
        Json = $response.Content | ConvertFrom-Json
    }
}

function Wait-ForHealth(
    [string]$url,
    [System.Diagnostics.Process]$process,
    [string]$stderrPath,
    [string]$label)
{
    for ($attempt = 0; $attempt -lt 80; $attempt++)
    {
        Start-Sleep -Milliseconds 500

        try
        {
            $response = Get-Json -url $url
            if ($response.StatusCode -eq 200)
            {
                return $response.Json
            }
        }
        catch
        {
            if ($process.HasExited)
            {
                $errorText = if (Test-Path -LiteralPath $stderrPath) { Get-Content $stderrPath -Raw } else { '' }
                throw "$label exited early with code $($process.ExitCode). $errorText"
            }
        }
    }

    throw "$label did not become ready in time at '$url'."
}

$artifactsRoot = if ($env:LawWatcherArtifactsRoot) { $env:LawWatcherArtifactsRoot } else { Join-Path $env:LOCALAPPDATA 'LawWatcher\artifacts' }
$apiRuntime = Resolve-PreferredHostRuntime `
    -publishedPath (Join-Path $repoRoot 'artifacts\publish\api-single\LawWatcher.Api.exe') `
    -buildPath (Join-Path $artifactsRoot 'bin\LawWatcher.Api\Debug\net10.0\LawWatcher.Api.dll') `
    -label 'API'
$workerLiteRuntime = Resolve-PreferredHostRuntime `
    -publishedPath (Join-Path $repoRoot 'artifacts\publish\worker-lite-single\LawWatcher.Worker.Lite.exe') `
    -buildPath (Join-Path $artifactsRoot 'bin\LawWatcher.Worker.Lite\Debug\net10.0\LawWatcher.Worker.Lite.dll') `
    -label 'worker-lite'
$workerAiRuntime = Resolve-PreferredHostRuntime `
    -publishedPath (Join-Path $repoRoot 'artifacts\publish\worker-ai-single\LawWatcher.Worker.Ai.exe') `
    -buildPath (Join-Path $artifactsRoot 'bin\LawWatcher.Worker.Ai\Debug\net10.0\LawWatcher.Worker.Ai.dll') `
    -label 'worker-ai'

$runId = [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $env:LOCALAPPDATA "LawWatcher\artifacts-smoke\runs\host-health-$runId"
$stateRoot = Join-Path $runRoot 'state'
$documentsRoot = Join-Path $runRoot 'documents'
$outputRoot = Join-Path $repoRoot 'output\health'
New-Item -ItemType Directory -Force -Path $stateRoot, $documentsRoot, $outputRoot | Out-Null

$apiPort = 5310
$workerLiteHealthPort = 5311
$workerAiHealthPort = 5312
$apiOut = Join-Path $repoRoot 'api-health-smoke-out.log'
$apiErr = Join-Path $repoRoot 'api-health-smoke-err.log'
$workerLiteOut = Join-Path $repoRoot 'worker-lite-health-smoke-out.log'
$workerLiteErr = Join-Path $repoRoot 'worker-lite-health-smoke-err.log'
$workerAiOut = Join-Path $repoRoot 'worker-ai-health-smoke-out.log'
$workerAiErr = Join-Path $repoRoot 'worker-ai-health-smoke-err.log'
Remove-Item $apiOut, $apiErr, $workerLiteOut, $workerLiteErr, $workerAiOut, $workerAiErr -ErrorAction SilentlyContinue

$env:LawWatcher__Storage__Provider = 'files'
$env:LawWatcher__Storage__StateRoot = $stateRoot
$env:Storage__LocalDocumentsRoot = $documentsRoot

$env:ASPNETCORE_URLS = "http://127.0.0.1:$apiPort"
$env:LawWatcher__Health__Urls = $null
$apiProcess = Start-HostProcess -runtime $apiRuntime -workingDirectory $repoRoot -stdoutPath $apiOut -stderrPath $apiErr

$env:ASPNETCORE_URLS = $null
$env:LawWatcher__Health__Urls = "http://127.0.0.1:$workerLiteHealthPort"
$workerLiteProcess = Start-HostProcess -runtime $workerLiteRuntime -workingDirectory $repoRoot -stdoutPath $workerLiteOut -stderrPath $workerLiteErr

$env:LawWatcher__Health__Urls = "http://127.0.0.1:$workerAiHealthPort"
$workerAiProcess = Start-HostProcess -runtime $workerAiRuntime -workingDirectory $repoRoot -stdoutPath $workerAiOut -stderrPath $workerAiErr

try
{
    $apiLive = Wait-ForHealth -url "http://127.0.0.1:$apiPort/health/live" -process $apiProcess -stderrPath $apiErr -label 'API'
    $apiReady = Wait-ForHealth -url "http://127.0.0.1:$apiPort/health/ready" -process $apiProcess -stderrPath $apiErr -label 'API'
    $workerLiteLive = Wait-ForHealth -url "http://127.0.0.1:$workerLiteHealthPort/health/live" -process $workerLiteProcess -stderrPath $workerLiteErr -label 'Worker.Lite'
    $workerLiteReady = Wait-ForHealth -url "http://127.0.0.1:$workerLiteHealthPort/health/ready" -process $workerLiteProcess -stderrPath $workerLiteErr -label 'Worker.Lite'
    $workerAiLive = Wait-ForHealth -url "http://127.0.0.1:$workerAiHealthPort/health/live" -process $workerAiProcess -stderrPath $workerAiErr -label 'Worker.Ai'
    $workerAiReady = Wait-ForHealth -url "http://127.0.0.1:$workerAiHealthPort/health/ready" -process $workerAiProcess -stderrPath $workerAiErr -label 'Worker.Ai'
    $capabilities = Get-Json -url "http://127.0.0.1:$apiPort/v1/system/capabilities"

    $summary = [ordered]@{
        verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        api = @{
            baseUrl = "http://127.0.0.1:$apiPort"
            liveStatus = $apiLive.status
            readyStatus = $apiReady.status
            readyEntries = @($apiReady.entries.PSObject.Properties.Name)
        }
        workerLite = @{
            baseUrl = "http://127.0.0.1:$workerLiteHealthPort"
            liveStatus = $workerLiteLive.status
            readyStatus = $workerLiteReady.status
            readyEntries = @($workerLiteReady.entries.PSObject.Properties.Name)
        }
        workerAi = @{
            baseUrl = "http://127.0.0.1:$workerAiHealthPort"
            liveStatus = $workerAiLive.status
            readyStatus = $workerAiReady.status
            readyEntries = @($workerAiReady.entries.PSObject.Properties.Name)
        }
        apiCapabilitiesProfile = [string]$capabilities.Json.runtimeProfile
        stateRoot = $stateRoot
        documentsRoot = $documentsRoot
    }

    $summaryPath = Join-Path $outputRoot 'host-health-summary.json'
    $summary | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryPath
    $summary | ConvertTo-Json -Depth 6
}
finally
{
    foreach ($process in @($workerAiProcess, $workerLiteProcess, $apiProcess))
    {
        if ($null -ne $process -and -not $process.HasExited)
        {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
    }

    $env:ASPNETCORE_URLS = $null
    $env:LawWatcher__Health__Urls = $null
}
