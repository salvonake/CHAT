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
$modelCertificationReport = Join-Path $OutputDir "model-certification-report.json"
$provenance = Join-Path $OutputDir "build-provenance.json"
$prerequisiteReport = Join-Path $OutputDir "prerequisite-validation.json"
$nativeBackendReport = Join-Path $OutputDir "native-backend-validation.json"
$signingReport = Join-Path $OutputDir "signing-report.json"
$machineConfig = Join-Path (Split-Path $OutputDir -Parent) "generated\appsettings.user.json"

$requiredArtifacts = @($msi, $bundle, $manifest, $provenance, $prerequisiteReport, $nativeBackendReport, $signingReport)
if ($InstallerMode -eq "full") {
    $requiredArtifacts += $modelCertificationReport
}

foreach ($artifact in $requiredArtifacts) {
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

        if (-not $configJson.MediatR -or -not $configJson.MediatR.LicenseKeySecretRef) {
            throw "Production machine config must contain MediatR:LicenseKeySecretRef."
        }

        if (-not [string]::IsNullOrWhiteSpace([string]$configJson.Security.EncryptionPassphrase)) {
            throw "Production machine config contains plaintext Security:EncryptionPassphrase."
        }

        if (-not [string]::IsNullOrWhiteSpace([string]$configJson.MediatR.LicenseKey)) {
            throw "Production machine config contains plaintext MediatR:LicenseKey."
        }
    }
}

$nativeBackends = @((Get-Content -Raw $nativeBackendReport | ConvertFrom-Json) | ForEach-Object { $_ })
foreach ($backend in $nativeBackends) {
    if ($backend.required -eq $true -and ($backend.present -ne $true -or $backend.sizeBytes -le 0)) {
        throw "Required native backend payload missing: $($backend.name)"
    }

    if ($backend.present -eq $true -and $backend.sha256 -notmatch '^[a-f0-9]{64}$') {
        throw "Native backend payload has invalid hash: $($backend.name)"
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
if ($json.schemaVersion -ne 3) {
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

    if ($model.type -eq "llm") {
        foreach ($property in @("architecture", "quantization", "ggufVersion", "certifiedBackend", "certifiedAtUtc", "compatibilityStatus", "tokenizerPolicy", "warningAccepted", "certificationReportHash")) {
            if (-not $model.PSObject.Properties[$property]) {
                throw "Manifest LLM entry missing certification property '$property'."
            }
        }

        if ($model.certificationReportHash -notmatch '^[a-f0-9]{64}$') {
            throw "Manifest LLM entry has invalid certification report SHA-256."
        }
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

if ($InstallerMode -eq "full") {
    $reportHash = (Get-FileHash $modelCertificationReport -Algorithm SHA256).Hash.ToLowerInvariant()
    $llm = @($json.models | Where-Object { $_.type -eq "llm" }) | Select-Object -First 1
    if ($null -eq $llm) {
        throw "Full manifest missing LLM entry for certification validation."
    }

    if ($reportHash -ne ([string]$llm.certificationReportHash).ToLowerInvariant()) {
        throw "Model certification report hash does not match manifest."
    }

    $certification = Get-Content -Raw $modelCertificationReport | ConvertFrom-Json
    if ($certification.schemaVersion -ne 1) {
        throw "Unexpected model certification report schemaVersion: $($certification.schemaVersion)"
    }

    if ($certification.sha256 -ne $llm.sha256) {
        throw "Certification report model hash does not match manifest LLM hash."
    }

    foreach ($pair in @(
        @{ manifest = "architecture"; report = "architecture" },
        @{ manifest = "quantization"; report = "quantization" },
        @{ manifest = "ggufVersion"; report = "ggufVersion" },
        @{ manifest = "certifiedBackend"; report = "backend" },
        @{ manifest = "certifiedAtUtc"; report = "generatedAtUtc" },
        @{ manifest = "compatibilityStatus"; report = "compatibilityStatus" }
    )) {
        $manifestValue = [string]$llm.PSObject.Properties[$pair.manifest].Value
        $reportValue = [string]$certification.PSObject.Properties[$pair.report].Value
        if ($manifestValue -ne $reportValue) {
            throw "Manifest certification field '$($pair.manifest)' does not match report field '$($pair.report)'."
        }
    }

    if ($llm.tokenizerPolicy -ne $certification.tokenizer.policy) {
        throw "Manifest tokenizer policy does not match certification report."
    }

    if ([bool]$llm.warningAccepted -ne [bool]$certification.tokenizer.warningAccepted) {
        throw "Manifest tokenizer warningAccepted does not match certification report."
    }

    if ($BuildProfile -eq "Production") {
        if ($certification.compatible -ne $true -or $certification.acceptedForPackaging -ne $true) {
            throw "Production model certification report must be compatible and accepted."
        }

        if ($certification.tokenizer.policy -ne "required" -or $certification.tokenizer.valid -ne $true) {
            throw "Production model certification must satisfy required tokenizer policy."
        }
    }
}

Write-Host "Installer artifacts validated: $OutputDir"
