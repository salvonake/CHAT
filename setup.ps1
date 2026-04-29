# Poseidon developer bootstrap.
# This script is intentionally non-destructive: it never scaffolds projects,
# rewrites the solution, or changes package references.

[CmdletBinding()]
param(
    [switch]$Restore,
    [switch]$Build,
    [switch]$Test,
    [switch]$InstallerReadiness
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root

try {
    Write-Host "Poseidon developer readiness" -ForegroundColor Cyan

    if (-not (Test-Path "Poseidon.sln")) {
        throw "Poseidon.sln was not found. Run this script from the repository root."
    }

    $expectedSdk = (Get-Content -Raw "dotnet_version.txt").Trim()
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw "dotnet CLI was not found in PATH. Install .NET SDK $expectedSdk."
    }

    $actualSdk = (& dotnet --version).Trim()
    if ($actualSdk -ne $expectedSdk) {
        throw "Expected .NET SDK $expectedSdk but found $actualSdk. Install the pinned SDK or update global.json and dotnet_version.txt together."
    }

    Write-Host "OK .NET SDK: $actualSdk"

    if (Get-Command docker -ErrorAction SilentlyContinue) {
        Write-Host "OK Docker CLI found"
    }
    else {
        Write-Warning "Docker CLI was not found. Qdrant integration tests require Docker or an external Qdrant instance."
    }

    $requiredFiles = @(
        "src/Poseidon.Desktop/Poseidon.Desktop.csproj",
        "src/Poseidon.ProvisioningCheck/Poseidon.ProvisioningCheck.csproj",
        "installer/Poseidon.Installer.wixproj",
        "installer/Poseidon.Bundle.wixproj",
        "installer/build-installer.ps1",
        "installer/Validate-InstallerArtifacts.ps1"
    )

    foreach ($file in $requiredFiles) {
        if (-not (Test-Path $file)) {
            throw "Required repository file is missing: $file"
        }
    }

    Write-Host "OK repository layout"

    if ($Restore) {
        dotnet restore Poseidon.sln
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }
    }

    if ($Build) {
        dotnet build Poseidon.sln -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
    }

    if ($Test) {
        dotnet test tests/Poseidon.UnitTests/Poseidon.UnitTests.csproj -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed." }
    }

    if ($InstallerReadiness) {
        & "tests/scripts/Test-RepositoryHygiene.ps1"
        if ($LASTEXITCODE -ne 0) { throw "repository hygiene validation failed." }
    }

    Write-Host "Poseidon developer readiness checks completed." -ForegroundColor Green
}
finally {
    Pop-Location
}
