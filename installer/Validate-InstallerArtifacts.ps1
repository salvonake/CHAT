#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDir = "$PSScriptRoot\output",
    [ValidateSet("Production", "NonProduction")]
    [string]$BuildProfile = "Production",
    [ValidateSet("full", "degraded")]
    [string]$InstallerMode = "full",
    [switch]$AllowTestModels,
    [switch]$AllowUnsigned
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($BuildProfile -eq "Production" -and $AllowUnsigned) {
    throw "-AllowUnsigned is only valid with -BuildProfile NonProduction."
}

if ($BuildProfile -eq "Production" -and $AllowTestModels) {
    throw "-AllowTestModels is only valid with -BuildProfile NonProduction."
}

$msi = Join-Path $OutputDir "Poseidon.Installer.msi"
$bundle = Join-Path $OutputDir "Poseidon.Bundle.exe"
$manifest = Join-Path $OutputDir "model-manifest.json"
$provenance = Join-Path $OutputDir "build-provenance.json"
$prerequisiteReport = Join-Path $OutputDir "prerequisite-validation.json"
$signingReport = Join-Path $OutputDir "signing-report.json"
$machineConfig = Join-Path (Split-Path $OutputDir -Parent) "generated\appsettings.user.json"

foreach ($artifact in @($msi, $bundle, $manifest, $provenance, $prerequisiteReport, $signingReport)) {
    if (-not (Test-Path $artifact -PathType Leaf)) {
        throw "Required installer artifact missing: $artifact"
    }

    if ((Get-Item $artifact).Length -le 0) {
        throw "Required installer artifact is empty: $artifact"
    }
}

function Assert-Signature([string]$Path) {
    $signature = Get-AuthenticodeSignature $Path
    if ($signature.Status -eq "Valid") {
        return
    }

    if ($AllowUnsigned -and $BuildProfile -eq "NonProduction") {
        return
    }

    throw "Artifact is not validly signed: $Path ($($signature.Status))"
}

Assert-Signature $msi
Assert-Signature $bundle

$signing = Get-Content -Raw $signingReport | ConvertFrom-Json
$provenanceJson = Get-Content -Raw $provenance | ConvertFrom-Json
if ($BuildProfile -eq "Production") {
    foreach ($entry in $signing) {
        if ($entry.status -ne "Valid" -or [string]::IsNullOrWhiteSpace($entry.signer)) {
            throw "Signing report contains unsigned or invalid release artifact: $($entry.path)"
        }
    }

    if (-not $provenanceJson.secretStorage -or
        $provenanceJson.secretStorage.provider -ne "dpapi" -or
        $provenanceJson.secretStorage.productionPlaintextSecrets -ne $false) {
        throw "Production provenance must declare DPAPI secret storage with no plaintext secrets."
    }

    if (Test-Path $machineConfig -PathType Leaf) {
        $configJson = Get-Content -Raw $machineConfig | ConvertFrom-Json
        if (-not $configJson.Security.EncryptionPassphraseRef) {
            throw "Production machine config must contain Security:EncryptionPassphraseRef."
        }

        if (-not [string]::IsNullOrWhiteSpace([string]$configJson.Security.EncryptionPassphrase)) {
            throw "Production machine config contains plaintext Security:EncryptionPassphrase."
        }
    }
}

$prereqs = Get-Content -Raw $prerequisiteReport | ConvertFrom-Json
if (-not $prereqs -or $prereqs.Count -lt 1) {
    throw "Prerequisite validation report is empty."
}

foreach ($prereq in $prereqs) {
    if ($prereq.verified -ne $true) {
        throw "Prerequisite was not verified: $($prereq.fileName)"
    }

    if ($prereq.sha256 -notmatch '^[a-f0-9]{64}$') {
        throw "Prerequisite report has invalid hash: $($prereq.fileName)"
    }
}

$json = Get-Content -Raw $manifest | ConvertFrom-Json
if ($json.schemaVersion -ne 2) {
    throw "Unexpected manifest schemaVersion: $($json.schemaVersion)"
}

if ($json.packaging -ne "wix-burn") {
    throw "Unexpected packaging value in manifest: $($json.packaging)"
}

if ($json.mode -ne $InstallerMode) {
    throw "Manifest installer mode mismatch. Expected $InstallerMode, found $($json.mode)."
}

if ($json.buildProfile -ne $BuildProfile) {
    throw "Manifest build profile mismatch. Expected $BuildProfile, found $($json.buildProfile)."
}

if (-not $json.models -or $json.models.Count -lt 1) {
    throw "Manifest does not contain model entries."
}

$seen = New-Object 'System.Collections.Generic.HashSet[string]'
$typeCounts = @{}
foreach ($model in $json.models) {
    foreach ($property in @("filename", "type", "required", "mode", "sizeBytes", "sha256", "targetPath")) {
        if (-not $model.PSObject.Properties[$property]) {
            throw "Manifest model entry missing property '$property'."
        }
    }

    if ($model.PSObject.Properties["sourcePath"]) {
        throw "Release manifest leaks local sourcePath for $($model.filename)."
    }

    if ($model.mode -ne $InstallerMode) {
        throw "Manifest model entry mode mismatch: $($model.filename)"
    }

    if ($model.required -ne $true) {
        throw "Manifest model entry must be explicitly required: $($model.filename)"
    }

    if ($model.sizeBytes -le 0) {
        throw "Manifest model entry has invalid size: $($model.filename)"
    }

    if (-not $AllowTestModels -and $model.type -eq "llm" -and $model.sizeBytes -lt 100MB) {
        throw "Production LLM model entry is below minimum production size: $($model.filename)"
    }

    if (-not $AllowTestModels -and $model.type -eq "embedding" -and $model.sizeBytes -lt 1MB) {
        throw "Production embedding model entry is below minimum production size: $($model.filename)"
    }

    if ($model.sha256 -notmatch '^[a-f0-9]{64}$') {
        throw "Manifest model entry has invalid SHA-256: $($model.filename)"
    }

    $key = "$($model.type)|$($model.filename)|$($model.targetPath)".ToLowerInvariant()
    if (-not $seen.Add($key)) {
        throw "Duplicate manifest model entry: $($model.filename)"
    }

    $type = [string]$model.type
    if (-not $typeCounts.ContainsKey($type)) {
        $typeCounts[$type] = 0
    }
    $typeCounts[$type]++
}

$llmCount = if ($typeCounts.ContainsKey("llm")) { $typeCounts["llm"] } else { 0 }
$embeddingCount = if ($typeCounts.ContainsKey("embedding")) { $typeCounts["embedding"] } else { 0 }

if ($InstallerMode -eq "full" -and ($llmCount -ne 1 -or $embeddingCount -ne 1)) {
    throw "Full manifest must contain exactly one LLM and one embedding entry."
}

if ($InstallerMode -eq "degraded" -and ($llmCount -ne 0 -or $embeddingCount -ne 1)) {
    throw "Degraded manifest must contain exactly one embedding entry and no LLM entry."
}

Write-Host "Installer artifacts validated: $OutputDir"
