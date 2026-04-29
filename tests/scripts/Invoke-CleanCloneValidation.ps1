#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RepositoryPath = "",
    [string]$CloneRoot = "",
    [ValidateSet("Production", "NonProduction")]
    [string]$BuildProfile = "NonProduction",
    [switch]$AllowUnsigned,
    [switch]$AllowTestModels,
    [switch]$SkipPrereqDownload,
    [switch]$SkipInstaller,
    [switch]$KeepClone
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$sourceRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($RepositoryPath)) {
    $RepositoryPath = $sourceRoot.Path
}

if ([string]::IsNullOrWhiteSpace($CloneRoot)) {
    $CloneRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("poseidon-clean-clone-" + [Guid]::NewGuid().ToString("N"))
}

$evidenceRoot = Join-Path $sourceRoot ".artifacts\release-evidence"
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$reportPath = Join-Path $evidenceRoot "clean-clone-validation-report.json"

if (Test-Path $CloneRoot) {
    $resolvedCloneRoot = (Resolve-Path $CloneRoot).Path
    $tempRoot = [System.IO.Path]::GetTempPath()
    if (-not $resolvedCloneRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove clone root outside temp directory: $resolvedCloneRoot"
    }
    Remove-Item -LiteralPath $resolvedCloneRoot -Recurse -Force
}

$steps = New-Object System.Collections.Generic.List[object]
function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Script
    )

    $started = Get-Date
    try {
        & $Script
        $steps.Add([pscustomobject]@{
            name = $Name
            status = "passed"
            startedAtUtc = $started.ToUniversalTime().ToString("o")
            completedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        })
    }
    catch {
        $steps.Add([pscustomobject]@{
            name = $Name
            status = "failed"
            startedAtUtc = $started.ToUniversalTime().ToString("o")
            completedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
            error = $_.Exception.Message
        })
        throw
    }
}

$clonePath = Join-Path $CloneRoot "Poseidon"
$success = $false

try {
    Invoke-Step "git clone" {
        git clone $RepositoryPath $clonePath
        if ($LASTEXITCODE -ne 0) { throw "git clone failed." }
    }

    Push-Location $clonePath
    try {
        Invoke-Step "repository hygiene" {
            ./tests/scripts/Test-RepositoryHygiene.ps1
        }

        Invoke-Step "dotnet restore" {
            dotnet restore Poseidon.sln
            if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }
        }

        Invoke-Step "dotnet build" {
            dotnet build Poseidon.sln -c Release --no-restore
            if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
        }

        Invoke-Step "unit tests" {
            dotnet test tests/Poseidon.UnitTests/Poseidon.UnitTests.csproj -c Release --no-build
            if ($LASTEXITCODE -ne 0) { throw "dotnet test failed." }
        }

        if (-not $SkipInstaller) {
            $modelPath = ".artifacts\installer-models"
            New-Item -ItemType Directory -Force -Path $modelPath | Out-Null
            Set-Content -Path (Join-Path $modelPath "qwen2.5-14b.Q5_K_M.gguf") -Value "ci-llm-model" -Encoding ASCII
            Set-Content -Path (Join-Path $modelPath "arabert.onnx") -Value "ci-embedding-model" -Encoding ASCII

            Invoke-Step "installer build" {
                $installerArgs = @{
                    ModelsPath = $modelPath
                    BuildProfile = $BuildProfile
                }
                if ($AllowTestModels) { $installerArgs.AllowTestModels = $true }
                if ($AllowUnsigned) { $installerArgs.UnsignedDevelopmentBuild = $true }
                if ($SkipPrereqDownload) { $installerArgs.SkipPrereqDownload = $true }
                ./installer/build-installer.ps1 @installerArgs
            }

            Invoke-Step "installer artifact validation" {
                $validationArgs = @{
                    BuildProfile = $BuildProfile
                }
                if ($AllowTestModels) { $validationArgs.AllowTestModels = $true }
                if ($AllowUnsigned) { $validationArgs.AllowUnsigned = $true }
                ./installer/Validate-InstallerArtifacts.ps1 @validationArgs
            }

            Invoke-Step "provisioning-check staged validation" {
                $check = "publish/provisioning-check/win-x64/provisioning-check.exe"
                & $check --config "installer/generated/appsettings.user.json" --manifest "installer/generated/model-manifest.json" --install-dir "installer/generated/staged-install" --mode full --log "installer/generated/provisioning-check-clean-clone.log"
                if ($LASTEXITCODE -ne 0) { throw "provisioning-check failed." }
            }
        }

        $success = $true
    }
    finally {
        Pop-Location
    }
}
finally {
    $report = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        repositoryPath = $RepositoryPath
        clonePath = $clonePath
        buildProfile = $BuildProfile
        success = $success
        steps = @($steps.ToArray())
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -Path $reportPath -Encoding UTF8

    if (-not $KeepClone -and (Test-Path $CloneRoot)) {
        Remove-Item -LiteralPath $CloneRoot -Recurse -Force
    }
}

if (-not $success) {
    throw "Clean clone validation failed. See $reportPath"
}

Write-Host "Clean clone validation passed. Report: $reportPath"
