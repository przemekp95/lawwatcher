param(
    [string]$WorkspaceRoot,
    [switch]$SkipBuild,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

function Assert-RobocopySuccess {
    param([int]$ExitCode)

    if ($ExitCode -gt 7) {
        throw "robocopy failed with exit code $ExitCode."
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$verificationRoot = if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    Join-Path $env:LOCALAPPDATA "Temp\\lawwatcher-verify-$timestamp"
} else {
    $WorkspaceRoot
}

New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null

Write-Host "Mirroring repository to $verificationRoot"
robocopy $repoRoot $verificationRoot /MIR /XD .git .artifacts TestResults output artifacts | Out-Null
Assert-RobocopySuccess -ExitCode $LASTEXITCODE

Push-Location $verificationRoot
try {
    if (-not $SkipBuild) {
        Write-Host "Running dotnet build"
        & dotnet build LawWatcher.slnx -c Release -m:1 /nr:false -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE."
        }
    }

    if (-not $SkipTests) {
        Write-Host "Running dotnet test"
        & dotnet test LawWatcher.slnx -c Release --no-build --collect:"XPlat Code Coverage"
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet test failed with exit code $LASTEXITCODE."
        }
    }

    Write-Host "Verification workspace: $verificationRoot"
}
finally {
    Pop-Location
}
