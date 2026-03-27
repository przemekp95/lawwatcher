$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.nuget\packages'
$env:ASPNETCORE_ENVIRONMENT = 'Development'

function Resolve-DockerPath
{
    $command = Get-Command docker -ErrorAction SilentlyContinue
    if ($command)
    {
        return $command.Source
    }

    $candidates = @(
        'C:\Program Files\Docker\Docker\resources\bin\docker.exe',
        'C:\Program Files\Docker\Docker\resources\docker.exe'
    )

    foreach ($candidate in $candidates)
    {
        if (Test-Path -LiteralPath $candidate)
        {
            return $candidate
        }
    }

    return $null
}

function Ensure-MinioAvailable
{
    $dockerPath = Resolve-DockerPath
    if ($null -eq $dockerPath)
    {
        throw 'Docker is unavailable. Start Docker Desktop before running the MinIO grounding smoke.'
    }

    $dockerToolsDirectory = Split-Path -Parent $dockerPath
    if (-not (($env:Path -split ';') -contains $dockerToolsDirectory))
    {
        $env:Path = "$dockerToolsDirectory;$env:Path"
    }

    $minioHealthUrl = 'http://127.0.0.1:9000/minio/health/live'
    try
    {
        $probe = Invoke-WebRequest -Uri $minioHealthUrl -UseBasicParsing -TimeoutSec 3
        if ($probe.StatusCode -eq 200)
        {
            return
        }
    }
    catch
    {
    }

    $composeFile = Join-Path $repoRoot 'ops\compose\docker-compose.yml'
    $composeEnvFile = Join-Path $repoRoot 'ops\env\dev-laptop.env.example'
    $composeStdOut = Join-Path $repoRoot 'minio-grounding-compose-out.log'
    $composeStdErr = Join-Path $repoRoot 'minio-grounding-compose-err.log'
    Remove-Item $composeStdOut, $composeStdErr -ErrorAction SilentlyContinue

    $composeArguments = "compose -f `"$composeFile`" --env-file `"$composeEnvFile`" up -d minio"
    $composeProcess = Start-Process `
        -FilePath $dockerPath `
        -ArgumentList $composeArguments `
        -WorkingDirectory $repoRoot `
        -RedirectStandardOutput $composeStdOut `
        -RedirectStandardError $composeStdErr `
        -WindowStyle Hidden `
        -PassThru
    $composeProcess.WaitForExit()

    $composeOutput = @(
        if (Test-Path -LiteralPath $composeStdOut) { Get-Content $composeStdOut -Raw }
        if (Test-Path -LiteralPath $composeStdErr) { Get-Content $composeStdErr -Raw }
    ) -join [Environment]::NewLine

    for ($attempt = 0; $attempt -lt 90; $attempt++)
    {
        Start-Sleep -Seconds 1
        try
        {
            $probe = Invoke-WebRequest -Uri $minioHealthUrl -UseBasicParsing -TimeoutSec 3
            if ($probe.StatusCode -eq 200)
            {
                return
            }
        }
        catch
        {
        }
    }

    if ($composeProcess.ExitCode -ne 0)
    {
        throw "Docker compose reported a non-zero exit code while starting MinIO and the health endpoint never became ready. $($composeOutput.Trim())"
    }

    throw 'MinIO did not become healthy on http://127.0.0.1:9000 in time.'
}

function Wait-ApiReady
{
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process] $ApiProcess,

        [Parameter(Mandatory = $true)]
        [string] $ApiErrorLog
    )

    for ($attempt = 0; $attempt -lt 60; $attempt++)
    {
        Start-Sleep -Milliseconds 500

        try
        {
            $acts = Invoke-RestMethod -Uri 'http://127.0.0.1:5299/v1/acts' -TimeoutSec 5
            return @($acts)
        }
        catch
        {
            if ($ApiProcess.HasExited)
            {
                $apiError = if (Test-Path -LiteralPath $ApiErrorLog) { Get-Content $ApiErrorLog -Raw } else { '' }
                throw "API exited early with code $($ApiProcess.ExitCode). $apiError"
            }
        }
    }

    throw 'API did not become ready in time.'
}

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
Ensure-MinioAvailable

$apiExe = Join-Path $repoRoot 'artifacts\publish\api-single\LawWatcher.Api.exe'
$workerExe = Join-Path $repoRoot 'artifacts\publish\worker-ai-single\LawWatcher.Worker.Ai.exe'
$apiDll = Join-Path $env:LOCALAPPDATA 'LawWatcher\artifacts\bin\LawWatcher.Api\Debug\net10.0\LawWatcher.Api.dll'
$workerDll = Join-Path $env:LOCALAPPDATA 'LawWatcher\artifacts\bin\LawWatcher.Worker.Ai\Debug\net10.0\LawWatcher.Worker.Ai.dll'

if (-not (Test-Path -LiteralPath $apiExe) -and -not (Test-Path -LiteralPath $apiDll))
{
    throw 'Missing API build output. Build or publish the host first.'
}

if (-not (Test-Path -LiteralPath $workerExe) -and -not (Test-Path -LiteralPath $workerDll))
{
    throw 'Missing worker-ai build output. Build or publish the host first.'
}

$runRoot = Join-Path $env:LOCALAPPDATA ('LawWatcher\artifacts-smoke\runs\act-grounding-minio-' + [Guid]::NewGuid().ToString('N'))
$stateRoot = Join-Path $runRoot 'state'
$localDocumentsRoot = Join-Path $runRoot 'documents-local-fallback'
$apiOut = Join-Path $repoRoot 'api-act-grounding-minio-smoke-out.log'
$apiErr = Join-Path $repoRoot 'api-act-grounding-minio-smoke-err.log'
$workerOut = Join-Path $repoRoot 'worker-ai-act-grounding-minio-smoke-out.log'
$workerErr = Join-Path $repoRoot 'worker-ai-act-grounding-minio-smoke-err.log'
$summaryPath = Join-Path $repoRoot 'output\smoke\act-ai-grounding-minio-summary.json'

New-Item -ItemType Directory -Force -Path $stateRoot, (Split-Path -Parent $summaryPath) | Out-Null
Remove-Item $apiOut, $apiErr, $workerOut, $workerErr -ErrorAction SilentlyContinue

$env:LawWatcher__Storage__Provider = 'files'
$env:LawWatcher__Storage__StateRoot = $stateRoot
$env:Storage__LocalDocumentsRoot = $localDocumentsRoot
$env:Storage__Minio__Endpoint = 'http://127.0.0.1:9000'
$env:Storage__Minio__AccessKey = 'lawwatcher'
$env:Storage__Minio__SecretKey = 'ChangeMe!123456'
$env:Storage__Minio__Region = 'us-east-1'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5299'

$apiProcess =
    if (Test-Path -LiteralPath $apiExe)
    {
        Start-Process -FilePath $apiExe -WorkingDirectory $repoRoot -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -WindowStyle Hidden -PassThru
    }
    else
    {
        Start-Process -FilePath 'dotnet' -ArgumentList @($apiDll) -WorkingDirectory $repoRoot -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -WindowStyle Hidden -PassThru
    }

$env:ASPNETCORE_URLS = $null
$workerProcess =
    if (Test-Path -LiteralPath $workerExe)
    {
        Start-Process -FilePath $workerExe -WorkingDirectory $repoRoot -RedirectStandardOutput $workerOut -RedirectStandardError $workerErr -WindowStyle Hidden -PassThru
    }
    else
    {
        Start-Process -FilePath 'dotnet' -ArgumentList @($workerDll) -WorkingDirectory $repoRoot -RedirectStandardOutput $workerOut -RedirectStandardError $workerErr -WindowStyle Hidden -PassThru
    }

try
{
    [object[]]$actsList = Wait-ApiReady -ApiProcess $apiProcess -ApiErrorLog $apiErr
    $act = $actsList[0]
    $requestBody = @{
        kind = 'act-summary'
        subjectType = 'act'
        subjectId = $act.id
        subjectTitle = $act.title
        prompt = 'Podsumuj opublikowany akt i uwzglednij material zrodlowy.'
    } | ConvertTo-Json

    $accepted = Invoke-RestMethod `
        -Uri 'http://127.0.0.1:5299/v1/ai/tasks' `
        -Method Post `
        -Headers @{ Authorization = 'Bearer portal-integrator-demo-token' } `
        -ContentType 'application/json' `
        -Body $requestBody `
        -TimeoutSec 10

    $requestedTaskId = @($accepted.id)[0]
    $completedTask = $null
    for ($attempt = 0; $attempt -lt 80; $attempt++)
    {
        Start-Sleep -Milliseconds 750
        [object[]]$tasks = Invoke-RestMethod -Uri 'http://127.0.0.1:5299/v1/ai/tasks' -TimeoutSec 10
        $task = @(
            $tasks |
                Where-Object { $_.id -eq $requestedTaskId } |
                Select-Object -First 1
        )
        $task = if ($task.Count -gt 0) { $task[0] } else { $null }

        if ($null -ne $task -and $task.status -eq 'completed')
        {
            $completedTask = $task
            break
        }

        if ($workerProcess.HasExited)
        {
            $workerError = if (Test-Path -LiteralPath $workerErr) { Get-Content $workerErr -Raw } else { '' }
            throw "Worker.Ai exited early with code $($workerProcess.ExitCode). $workerError"
        }
    }

    if ($null -eq $completedTask)
    {
        throw 'AI task did not reach completed state in time.'
    }

    [object[]]$citations = @($completedTask.citations | Select-Object -Unique)
    $content = @($completedTask.content)[0]
    $containsDocumentCitation = $citations | Where-Object { $_ -like 'document://legal-corpus/*' } | Select-Object -First 1
    $localFallbackHasArtifacts = Test-Path -LiteralPath (Join-Path $localDocumentsRoot 'legal-corpus')
    $minioHealth = Invoke-WebRequest -Uri 'http://127.0.0.1:9000/minio/health/live' -UseBasicParsing -TimeoutSec 5

    if (-not ($citations -contains $act.eli))
    {
        throw 'Expected grounded AI response to include the act ELI citation.'
    }

    if ($null -eq $containsDocumentCitation)
    {
        throw 'Expected grounded AI response to include a document://legal-corpus citation.'
    }

    if ($localFallbackHasArtifacts)
    {
        throw 'Expected local filesystem document fallback to stay unused when MinIO is configured, but legal-corpus artifacts were written locally.'
    }

    $summary = [ordered]@{
        verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        taskId = @($completedTask.id)[0]
        subjectType = @($completedTask.subjectType)[0]
        model = @($completedTask.model)[0]
        citations = $citations
        containsEliCitation = $true
        containsDocumentCitation = $containsDocumentCitation
        contentPreview = if ($null -eq $content) { '' } else { $content.Substring(0, [Math]::Min(220, $content.Length)) }
        minioHealthStatusCode = $minioHealth.StatusCode
        localFallbackHasArtifacts = $localFallbackHasArtifacts
        stateRoot = $stateRoot
        localDocumentsRoot = $localDocumentsRoot
        apiStdoutLog = $apiOut
        apiStderrLog = $apiErr
        workerStdoutLog = $workerOut
        workerStderrLog = $workerErr
    }

    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    $summary | ConvertTo-Json -Depth 6
}
finally
{
    foreach ($process in @($workerProcess, $apiProcess))
    {
        if ($null -ne $process -and -not $process.HasExited)
        {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
    }

    $env:ASPNETCORE_URLS = $null
    $env:LawWatcher__Storage__Provider = $null
    $env:LawWatcher__Storage__StateRoot = $null
    $env:Storage__LocalDocumentsRoot = $null
    $env:Storage__Minio__Endpoint = $null
    $env:Storage__Minio__AccessKey = $null
    $env:Storage__Minio__SecretKey = $null
    $env:Storage__Minio__Region = $null
}
