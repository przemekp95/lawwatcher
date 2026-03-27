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

$apiExe = Join-Path $repoRoot 'artifacts\publish\api-single\LawWatcher.Api.exe'
$workerExe = Join-Path $repoRoot 'artifacts\publish\worker-ai-single\LawWatcher.Worker.Ai.exe'
$apiDll = Join-Path $env:LOCALAPPDATA 'LawWatcher\artifacts\bin\LawWatcher.Api\Debug\net10.0\LawWatcher.Api.dll'
$workerDll = Join-Path $env:LOCALAPPDATA 'LawWatcher\artifacts\bin\LawWatcher.Worker.Ai\Debug\net10.0\LawWatcher.Worker.Ai.dll'

if (-not (Test-Path -LiteralPath $apiExe) -and -not (Test-Path -LiteralPath $apiDll))
{
    throw "Missing API build output. Build or publish the host first."
}

if (-not (Test-Path -LiteralPath $workerExe) -and -not (Test-Path -LiteralPath $workerDll))
{
    throw "Missing worker-ai build output. Build or publish the host first."
}

$runRoot = Join-Path $env:LOCALAPPDATA ('LawWatcher\artifacts-smoke\runs\act-grounding-' + [Guid]::NewGuid().ToString('N'))
$stateRoot = Join-Path $runRoot 'state'
$documentsRoot = Join-Path $runRoot 'documents'
$apiOut = Join-Path $repoRoot 'api-act-grounding-smoke-out.log'
$apiErr = Join-Path $repoRoot 'api-act-grounding-smoke-err.log'
$workerOut = Join-Path $repoRoot 'worker-ai-act-grounding-smoke-out.log'
$workerErr = Join-Path $repoRoot 'worker-ai-act-grounding-smoke-err.log'

New-Item -ItemType Directory -Force -Path $stateRoot, $documentsRoot | Out-Null
Remove-Item $apiOut, $apiErr, $workerOut, $workerErr -ErrorAction SilentlyContinue

$env:LawWatcher__Storage__Provider = 'files'
$env:LawWatcher__Storage__StateRoot = $stateRoot
$env:Storage__LocalDocumentsRoot = $documentsRoot
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
    $acts = $null
    for ($attempt = 0; $attempt -lt 60; $attempt++)
    {
        Start-Sleep -Milliseconds 500

        try
        {
            $acts = Invoke-RestMethod -Uri 'http://127.0.0.1:5299/v1/acts' -TimeoutSec 5
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

    if ($null -eq $acts)
    {
        throw 'API did not become ready in time.'
    }

    [object[]]$actsList = $acts
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
                Where-Object {
                    $_.id -eq $requestedTaskId
                } |
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

    $taskId = @($completedTask.id)[0]
    $subjectType = @($completedTask.subjectType)[0]
    $model = @($completedTask.model)[0]
    $content = @($completedTask.content)[0]
    [object[]]$citations = $completedTask.citations
    $citations = @($citations | Select-Object -Unique)

    [ordered]@{
        taskId = $taskId
        subjectType = $subjectType
        model = $model
        citations = $citations
        containsEliCitation = $citations -contains $act.eli
        containsDocumentCitation = ($citations | Where-Object { $_ -like 'document://legal-corpus/*' } | Select-Object -First 1)
        contentPreview = if ($null -eq $content) { '' } else { $content.Substring(0, [Math]::Min(220, $content.Length)) }
        documentsRootExists = Test-Path (Join-Path $documentsRoot 'legal-corpus')
        stateRoot = $stateRoot
        documentsRoot = $documentsRoot
    } | ConvertTo-Json -Depth 6
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
}
