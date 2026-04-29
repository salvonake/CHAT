#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$EvidencePath = "deploy\enterprise\deployment-evidence.json",
    [string]$OutputDirectory = ".artifacts\release-evidence",
    [switch]$RequireCertified
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

    $requiredScenarios = @(
        "interactive-install",
        "silent-msi-install",
        "silent-burn-install",
        "repair",
        "upgrade",
        "rollback",
        "uninstall",
        "sccm-deployment",
        "intune-deployment",
        "gpo-deployment",
        "system-context-deployment",
        "multi-user-machine-deployment",
        "existing-user-migration",
        "existing-config-preservation",
        "external-model-mode",
        "full-local-mode",
        "degraded-mode"
    )

    $resolvedEvidencePath = if ([System.IO.Path]::IsPathRooted($EvidencePath)) {
        $EvidencePath
    } else {
        Join-Path $root $EvidencePath
    }

    $submitted = @()
    if (Test-Path $resolvedEvidencePath -PathType Leaf) {
        $json = Get-Content -Raw $resolvedEvidencePath | ConvertFrom-Json
        $submitted = @($json.scenarios)
    }

    $matrix = foreach ($scenario in $requiredScenarios) {
        $entry = $submitted | Where-Object { $_.id -eq $scenario } | Select-Object -First 1
        if ($entry) {
            [pscustomobject]@{
                id = $scenario
                status = [string]$entry.status
                installResult = [string]$entry.installResult
                provisioningResult = [string]$entry.provisioningResult
                startupMode = [string]$entry.startupMode
                repairResult = [string]$entry.repairResult
                rollbackResult = [string]$entry.rollbackResult
                uninstallPolicy = [string]$entry.uninstallPolicy
                logArtifact = [string]$entry.logArtifact
            }
        }
        else {
            [pscustomobject]@{
                id = $scenario
                status = "missing-evidence"
                installResult = ""
                provisioningResult = ""
                startupMode = ""
                repairResult = ""
                rollbackResult = ""
                uninstallPolicy = ""
                logArtifact = ""
            }
        }
    }

    $failures = @($matrix | Where-Object {
        $_.status -ne "passed" -or
        [string]::IsNullOrWhiteSpace($_.logArtifact)
    })

    $report = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        evidencePath = $resolvedEvidencePath
        requiredScenarioCount = $requiredScenarios.Count
        certifiedScenarioCount = @($matrix | Where-Object { $_.status -eq "passed" }).Count
        missingOrFailedScenarioCount = $failures.Count
        certified = ($failures.Count -eq 0)
        scenarios = @($matrix)
    }

    $report | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $outputPath "enterprise-deployment-certification-matrix.json") -Encoding UTF8

    if ($RequireCertified -and $failures.Count -gt 0) {
        throw "Enterprise deployment certification is incomplete. Missing/failed scenarios: $($failures.id -join ', ')"
    }

    Write-Host "Enterprise deployment evidence report generated: $(Join-Path $outputPath "enterprise-deployment-certification-matrix.json")"
}
finally {
    Pop-Location
}
