#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDirectory = ".artifacts\release-evidence",
    [string]$ReleaseVersion = "",
    [switch]$FailOnDirty
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Push-Location $root

try {
    $outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
        $OutputDirectory
    } else {
        Join-Path $root $OutputDirectory
    }

    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

    $status = @(git status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "git status failed."
    }

    if ($FailOnDirty -and $status.Count -gt 0) {
        throw "Release evidence requires a clean working tree. Dirty entries: $($status.Count)"
    }

    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "git rev-parse HEAD failed."
    }

    $branch = (git branch --show-current).Trim()
    $dotnetVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet --version failed."
    }

    $trackedFiles = @(git ls-files)
    if ($LASTEXITCODE -ne 0) {
        throw "git ls-files failed."
    }

    $legacyPatterns = @(
        "LegalAI.sln",
        "src/LegalAI*",
        "tests/LegalAI*",
        "installer/LegalAI*",
        "installer/Resources/LegalAI*",
        "deploy/qa/Collect-LegalAI*"
    )
    $legacyMatches = @(git ls-files -- $legacyPatterns)
    if ($LASTEXITCODE -ne 0) {
        throw "git ls-files legacy scan failed."
    }

    $generatedPatterns = @(
        ".artifacts/*",
        "publish/*",
        "TestResults/*",
        "installer/generated/*",
        "installer/output/*",
        "installer/obj/*",
        "deploy/qa/evidence/*"
    )
    $generatedMatches = @(git ls-files -- $generatedPatterns)
    if ($LASTEXITCODE -ne 0) {
        throw "git ls-files generated scan failed."
    }

    $sourceFiles = @($trackedFiles | Where-Object {
        $_ -like "src/Poseidon.*" -or
        $_ -like "tests/Poseidon.*" -or
        $_ -eq "Poseidon.sln" -or
        $_ -like "installer/Poseidon.*" -or
        $_ -like "installer/*.wxs" -or
        $_ -like "installer/*.wxl" -or
        $_ -like "installer/*.ps1" -or
        $_ -like ".github/workflows/*" -or
        $_ -like "tests/scripts/*" -or
        $_ -like "docs/*" -or
        $_ -eq "README.md" -or
        $_ -eq "PROJECT_FULL_TECHNICAL_REPORT.txt"
    })

    $trackedManifest = foreach ($file in $sourceFiles | Sort-Object) {
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            $item = Get-Item -LiteralPath $file
            [pscustomobject]@{
                path = $file
                sizeBytes = $item.Length
                sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    }

    $migrationManifest = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        releaseVersion = $ReleaseVersion
        gitCommit = $commit
        gitBranch = $branch
        workingTreeClean = ($status.Count -eq 0)
        dirtyEntryCount = $status.Count
        solution = "Poseidon.sln"
        officialInstallerArtifacts = @("Poseidon.Installer.msi", "Poseidon.Bundle.exe")
        authoritativeInstaller = "WiX/Burn"
        predecessorOperationalTreeRemoved = ($legacyMatches.Count -eq 0)
        generatedArtifactTrackingClean = ($generatedMatches.Count -eq 0)
        pinnedDotnetSdk = (Get-Content -Raw "dotnet_version.txt").Trim()
        actualDotnetSdk = $dotnetVersion
    }

    $deletedLegacyManifest = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        forbiddenTrackedPatterns = $legacyPatterns
        activeTrackedMatches = @($legacyMatches | Sort-Object)
        compliant = ($legacyMatches.Count -eq 0)
    }

    $trackedSourceManifest = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        gitCommit = $commit
        fileCount = @($trackedManifest).Count
        files = @($trackedManifest)
    }

    $releaseBaselineManifest = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        releaseVersion = $ReleaseVersion
        gitCommit = $commit
        gitBranch = $branch
        workingTreeClean = ($status.Count -eq 0)
        trackedFileCount = $trackedFiles.Count
        activeSourceFileCount = @($sourceFiles).Count
        legacyTrackedFileCount = $legacyMatches.Count
        generatedTrackedFileCount = $generatedMatches.Count
        validationCommands = @(
            "dotnet restore Poseidon.sln",
            "dotnet build Poseidon.sln -c Release --no-restore",
            "dotnet test tests/Poseidon.UnitTests/Poseidon.UnitTests.csproj -c Release --no-build",
            "installer/build-installer.ps1 -BuildProfile Production",
            "installer/Validate-InstallerArtifacts.ps1 -BuildProfile Production",
            "tests/scripts/Test-RepositoryHygiene.ps1"
        )
    }

    $migrationManifest | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $outputPath "migration-manifest.json") -Encoding UTF8
    $deletedLegacyManifest | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $outputPath "deleted-legacy-manifest.json") -Encoding UTF8
    $trackedSourceManifest | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $outputPath "tracked-source-manifest.json") -Encoding UTF8
    $releaseBaselineManifest | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $outputPath "release-baseline-manifest.json") -Encoding UTF8

    if ($legacyMatches.Count -gt 0) {
        throw "Legacy operational files are still tracked: $($legacyMatches -join ', ')"
    }

    if ($generatedMatches.Count -gt 0) {
        throw "Generated artifact files are tracked: $($generatedMatches -join ', ')"
    }

    Write-Host "Release evidence generated: $outputPath"
}
finally {
    Pop-Location
}
