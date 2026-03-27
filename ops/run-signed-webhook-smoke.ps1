$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.nuget\packages'
$env:ASPNETCORE_ENVIRONMENT = 'Development'

$apiDll = Join-Path $env:LOCALAPPDATA 'LawWatcher\artifacts\bin\LawWatcher.Api\Debug\net10.0\LawWatcher.Api.dll'
$workerDll = Join-Path $env:LOCALAPPDATA 'LawWatcher\artifacts\bin\LawWatcher.Worker.Lite\Debug\net10.0\LawWatcher.Worker.Lite.dll'
if (-not (Test-Path -LiteralPath $apiDll))
{
    throw "Missing API build output at '$apiDll'. Build the host first."
}

if (-not (Test-Path -LiteralPath $workerDll))
{
    throw "Missing worker-lite build output at '$workerDll'. Build the host first."
}

function Get-HmacSha256Signature
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $Payload,

        [Parameter(Mandatory = $true)]
        [string] $Secret
    )

    $hmac = [System.Security.Cryptography.HMACSHA256]::new([System.Text.Encoding]::UTF8.GetBytes($Secret))
    try
    {
        $hashBytes = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Payload))
        $hex = -join ($hashBytes | ForEach-Object { $_.ToString('x2') })
        return "sha256=$hex"
    }
    finally
    {
        $hmac.Dispose()
    }
}

$apiPort = 5297
$listenerPort = 5307
$signingSecret = 'signed-webhook-smoke-secret'
$expectedDispatchCount = 2
$runRoot = Join-Path $env:LOCALAPPDATA ('LawWatcher\artifacts-smoke\runs\signed-webhook-' + [Guid]::NewGuid().ToString('N'))
$stateRoot = Join-Path $runRoot 'state'
$documentsRoot = Join-Path $runRoot 'documents'
$capturePath = Join-Path $runRoot 'captured-webhooks.json'
$persistedDispatchesPath = Join-Path $stateRoot 'integration-api\webhook-dispatches\dispatches.json'
$apiOut = Join-Path $repoRoot 'api-signed-webhook-smoke-out.log'
$apiErr = Join-Path $repoRoot 'api-signed-webhook-smoke-err.log'
$workerOut = Join-Path $repoRoot 'worker-lite-signed-webhook-smoke-out.log'
$workerErr = Join-Path $repoRoot 'worker-lite-signed-webhook-smoke-err.log'

New-Item -ItemType Directory -Force -Path $stateRoot, $documentsRoot | Out-Null
Remove-Item $capturePath, $apiOut, $apiErr, $workerOut, $workerErr -ErrorAction SilentlyContinue

$listenerJob = Start-Job -ArgumentList $listenerPort, $capturePath, $expectedDispatchCount -ScriptBlock {
    param(
        [int] $Port,
        [string] $CapturePath,
        [int] $ExpectedDispatchCount
    )

    $ErrorActionPreference = 'Stop'
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add("http://127.0.0.1:$Port/")
    $listener.Start()

    $captures = [System.Collections.Generic.List[object]]::new()
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)

    try
    {
        while ($captures.Count -lt $ExpectedDispatchCount -and [DateTimeOffset]::UtcNow -lt $deadline)
        {
            $pendingContext = $listener.GetContextAsync()
            if (-not $pendingContext.Wait([TimeSpan]::FromSeconds(5)))
            {
                continue
            }

            $context = $pendingContext.Result
            try
            {
                $reader = [System.IO.StreamReader]::new($context.Request.InputStream, $context.Request.ContentEncoding)
                try
                {
                    $body = $reader.ReadToEnd()
                }
                finally
                {
                    $reader.Dispose()
                }

                $headers = [ordered]@{}
                foreach ($headerName in $context.Request.Headers.AllKeys)
                {
                    $headers[$headerName] = $context.Request.Headers[$headerName]
                }

                $captures.Add([ordered]@{
                    method = $context.Request.HttpMethod
                    url = $context.Request.Url.AbsoluteUri
                    headers = $headers
                    body = $body
                    receivedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
                })

                $captures.ToArray() | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $CapturePath -Encoding utf8

                $responseBytes = [System.Text.Encoding]::UTF8.GetBytes('{"received":true}')
                $context.Response.StatusCode = 200
                $context.Response.ContentType = 'application/json'
                $context.Response.OutputStream.Write($responseBytes, 0, $responseBytes.Length)
            }
            finally
            {
                $context.Response.OutputStream.Close()
            }
        }
    }
    finally
    {
        $listener.Stop()
        $listener.Close()
    }
}

$env:LawWatcher__Storage__Provider = 'files'
$env:LawWatcher__Storage__StateRoot = $stateRoot
$env:Storage__LocalDocumentsRoot = $documentsRoot
$env:LawWatcher__Webhooks__Backend = 'SignedHttp'
$env:LawWatcher__Webhooks__SigningSecret = $signingSecret
$env:LawWatcher__SeedData__EnableWebhookSubscriptionSeed = 'false'
$env:ASPNETCORE_URLS = "http://127.0.0.1:$apiPort"
$apiProcess = Start-Process -FilePath 'dotnet' -ArgumentList @($apiDll) -WorkingDirectory $repoRoot -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -WindowStyle Hidden -PassThru

$env:ASPNETCORE_URLS = $null
$workerProcess = Start-Process -FilePath 'dotnet' -ArgumentList @($workerDll) -WorkingDirectory $repoRoot -RedirectStandardOutput $workerOut -RedirectStandardError $workerErr -WindowStyle Hidden -PassThru

try
{
    $alerts = $null
    for ($attempt = 0; $attempt -lt 60; $attempt++)
    {
        Start-Sleep -Milliseconds 500

        try
        {
            $alerts = @(Invoke-RestMethod -Uri "http://127.0.0.1:$apiPort/v1/alerts" -TimeoutSec 5 | ForEach-Object { $_ })
            break
        }
        catch
        {
            if ($apiProcess.HasExited)
            {
                $apiError = if (Test-Path -LiteralPath $apiErr) { Get-Content $apiErr -Raw } else { '' }
                throw "API exited early with code $($apiProcess.ExitCode). $apiError"
            }
        }
    }

    if ($null -eq $alerts)
    {
        throw 'API did not become ready in time.'
    }

    $registrationRequest = @{
        name = 'Signed webhook smoke'
        callbackUrl = "http://127.0.0.1:$listenerPort/hooks/alerts"
        eventTypes = @('alert.created')
    } | ConvertTo-Json

    Invoke-RestMethod `
        -Uri "http://127.0.0.1:$apiPort/v1/webhooks" `
        -Method Post `
        -Headers @{ Authorization = 'Bearer portal-integrator-demo-token' } `
        -ContentType 'application/json' `
        -Body $registrationRequest `
        -TimeoutSec 10 | Out-Null

    $listenerCompleted = $false
    for ($attempt = 0; $attempt -lt 80; $attempt++)
    {
        Start-Sleep -Milliseconds 750

        if ($workerProcess.HasExited)
        {
            $workerError = if (Test-Path -LiteralPath $workerErr) { Get-Content $workerErr -Raw } else { '' }
            throw "Worker.Lite exited early with code $($workerProcess.ExitCode). $workerError"
        }

        $listenerState = (Get-Job -Id $listenerJob.Id).State
        if ($listenerState -eq 'Completed')
        {
            $listenerCompleted = $true
            break
        }
    }

    if (-not $listenerCompleted)
    {
        throw 'Signed webhook listener did not complete in time.'
    }

    $captures = if (Test-Path -LiteralPath $capturePath)
    {
        @(Get-Content -LiteralPath $capturePath -Raw | ConvertFrom-Json | ForEach-Object { $_ })
    }
    else
    {
        @()
    }

    if ($captures.Count -lt $expectedDispatchCount)
    {
        throw "Signed webhook listener captured only $($captures.Count) request(s)."
    }

    $dispatches = @()
    for ($attempt = 0; $attempt -lt 10; $attempt++)
    {
        Start-Sleep -Milliseconds 500
        $dispatches = @(Invoke-RestMethod -Uri "http://127.0.0.1:$apiPort/v1/system/webhook-dispatches" -TimeoutSec 10 | ForEach-Object { $_ })
    }

    $persistedDispatches = if (Test-Path -LiteralPath $persistedDispatchesPath)
    {
        @(((Get-Content -LiteralPath $persistedDispatchesPath -Raw | ConvertFrom-Json).dispatches) | ForEach-Object { $_ })
    }
    else
    {
        @()
    }

    $payloads = @()
    $allSignaturesMatch = $true
    foreach ($capture in $captures)
    {
        $payloads += (ConvertFrom-Json $capture.body)
        $signature = $capture.headers.'X-LawWatcher-Signature'
        $expectedSignature = Get-HmacSha256Signature -Payload $capture.body -Secret $signingSecret
        if ($signature -ne $expectedSignature)
        {
            $allSignaturesMatch = $false
        }
    }

    [ordered]@{
        capturedRequestCount = $captures.Count
        firstCallbackUrl = @($captures[0].url)[0]
        firstEventTypeHeader = @($captures[0].headers.'X-LawWatcher-Event-Type')[0]
        firstSignatureHeader = @($captures[0].headers.'X-LawWatcher-Signature')[0]
        allSignaturesMatch = $allSignaturesMatch
        allPayloadsAreAlertCreated = (@($payloads | ForEach-Object { $_.type }) | Where-Object { $_ -ne 'alert.created' }).Count -eq 0
        apiDispatchRecordCount = @($dispatches | Where-Object { $_.callbackUrl -eq "http://127.0.0.1:$listenerPort/hooks/alerts" }).Count
        persistedDispatchRecordCount = @($persistedDispatches | Where-Object { $_.callbackUrl -eq "http://127.0.0.1:$listenerPort/hooks/alerts" }).Count
        workerObservedSignedHttp = Select-String -Path $workerOut -Pattern 'webhookEvents=' -SimpleMatch -Quiet
        stateRoot = $stateRoot
        capturePath = $capturePath
    } | ConvertTo-Json -Depth 6
}
finally
{
    if ($null -ne $listenerJob)
    {
        Stop-Job -Job $listenerJob -ErrorAction SilentlyContinue | Out-Null
        Receive-Job -Job $listenerJob -ErrorAction SilentlyContinue | Out-Null
        Remove-Job -Job $listenerJob -Force -ErrorAction SilentlyContinue | Out-Null
    }

    foreach ($process in @($workerProcess, $apiProcess))
    {
        if ($null -ne $process -and -not $process.HasExited)
        {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
    }
}
