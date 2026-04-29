#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$EvidenceDirectory = ".artifacts\release-evidence",
    [switch]$RequireCertified
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Push-Location $root

try {
    $evidencePath = if ([System.IO.Path]::IsPathRooted($EvidenceDirectory)) {
        $EvidenceDirectory
    } else {
        Join-Path $root $EvidenceDirectory
    }

    New-Item -ItemType Directory -Path $evidencePath -Force | Out-Null
    $blockers = New-Object System.Collections.Generic.List[string]

    function Read-Json($Path) {
        if (Test-Path $Path -PathType Leaf) {
            return Get-Content -Raw $Path | ConvertFrom-Json
        }
        return $null
    }

    $cleanClone = Read-Json (Join-Path $evidencePath "clean-clone-validation-report.json")
    $signing = Read-Json (Join-Path $evidencePath "signing-readiness-report.json")
    $deployment = Read-Json (Join-Path $evidencePath "enterprise-deployment-certification-matrix.json")
    $compliance = Read-Json (Join-Path $evidencePath "compliance\compliance-report.json")

    if (-not $cleanClone -or $cleanClone.success -ne $true) {
        $blockers.Add("Clean clone validation has not passed.")
    }

    if (-not $signing -or $signing.productionReady -ne $true) {
        $blockers.Add("Production signing readiness is not certified.")
    }

    if (-not $deployment -or $deployment.certified -ne $true) {
        $blockers.Add("Enterprise deployment matrix is not fully certified.")
    }

    if (-not $compliance -or $compliance.compliant -ne $true) {
        $blockers.Add("Compliance package is incomplete or blocked.")
    }

    $report = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        certified = ($blockers.Count -eq 0)
        cleanClonePassed = ($cleanClone -and $cleanClone.success -eq $true)
        productionSigningReady = ($signing -and $signing.productionReady -eq $true)
        deploymentMatrixCertified = ($deployment -and $deployment.certified -eq $true)
        compliancePackageComplete = ($compliance -and $compliance.compliant -eq $true)
        blockers = @($blockers)
    }

    $report | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $evidencePath "final-enterprise-certification-report.json") -Encoding UTF8

    if ($RequireCertified -and $blockers.Count -gt 0) {
        throw "Final enterprise certification failed: $($blockers -join '; ')"
    }

    Write-Host "Final enterprise certification report generated: $(Join-Path $evidencePath "final-enterprise-certification-report.json")"
}
finally {
    Pop-Location
}
