#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the authoritative Poseidon WiX MSI and Burn bundle.

.DESCRIPTION
    Production builds publish Poseidon.Desktop and Poseidon.ProvisioningCheck,
    generate a machine-level runtime config, generate strict model/provenance
    artifacts, verify pinned prerequisites, validate the staged provisioning
    contract, build WiX MSI/Burn artifacts, sign release artifacts, and verify
    signatures before release output is accepted.
#>
[CmdletBinding()]
param(
    [string]$ModelsPath = ".\models",
    [switch]$Degraded,
    [switch]$AllowTestModels,
    [switch]$SkipPrereqDownload,
    [switch]$SkipPublish,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("Production", "NonProduction")]
    [string]$BuildProfile = "Production",
    [switch]$UnsignedDevelopmentBuild,
    [string]$PrerequisitesConfigPath = "",
    [string]$EncryptionPassphrase = "",
    [string]$SignToolPath = "",
    [string]$SigningCertificateThumbprint = "",
    [string]$SigningCertificatePath = "",
    [string]$SigningCertificatePassword = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [ValidateSet("LocalMachine", "CurrentUser")]
    [string]$SecretScope = "LocalMachine",
    [string]$SecretKeyVersion = "v1"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path $PSScriptRoot -Parent
$InstallerDir = $PSScriptRoot
$PublishDir = Join-Path $Root "publish\win-x64"
$ProvisioningPublishDir = Join-Path $Root "publish\provisioning-check\win-x64"
$OutputDir = Join-Path $InstallerDir "output"
$ObjDir = Join-Path $InstallerDir "obj"
$GeneratedDir = Join-Path $InstallerDir "generated"
$StagedInstallDir = Join-Path $GeneratedDir "staged-install"
$PrereqsDir = Join-Path $InstallerDir "prereqs"
$DesktopProj = Join-Path $Root "src\Poseidon.Desktop\Poseidon.Desktop.csproj"
$ProvisioningProj = Join-Path $Root "src\Poseidon.ProvisioningCheck\Poseidon.ProvisioningCheck.csproj"
$ProvisioningExe = Join-Path $ProvisioningPublishDir "provisioning-check.exe"
$MachineConfigPath = Join-Path $GeneratedDir "appsettings.user.json"
$ModelManifestPath = Join-Path $GeneratedDir "model-manifest.json"
$BuildProvenancePath = Join-Path $GeneratedDir "build-provenance.json"
$PrerequisiteReportPath = Join-Path $GeneratedDir "prerequisite-validation.json"
$SigningReportPath = Join-Path $GeneratedDir "signing-report.json"

if ([string]::IsNullOrWhiteSpace($PrerequisitesConfigPath)) {
    $PrerequisitesConfigPath = Join-Path $InstallerDir "prerequisites.json"
}

if ([string]::IsNullOrWhiteSpace($EncryptionPassphrase)) {
    $EncryptionPassphrase = [Environment]::GetEnvironmentVariable("POSEIDON_INSTALLER_ENCRYPTION_PASSPHRASE")
}

if ($BuildProfile -eq "NonProduction" -and [string]::IsNullOrWhiteSpace($EncryptionPassphrase)) {
    $EncryptionPassphrase = "NonProductionInstallerLocalKey!2026_CiPackagingOnly"
}

if ($BuildProfile -eq "Production" -and $UnsignedDevelopmentBuild) {
    throw "-UnsignedDevelopmentBuild is only valid with -BuildProfile NonProduction."
}

if ($AllowTestModels -and $BuildProfile -eq "Production") {
    throw "-AllowTestModels is only valid with -BuildProfile NonProduction."
}

if (-not [System.IO.Path]::IsPathRooted($ModelsPath)) {
    $rootRelativeModelsPath = Join-Path $Root $ModelsPath
    $installerRelativeModelsPath = Join-Path $InstallerDir $ModelsPath
    $ModelsPath = if (Test-Path $rootRelativeModelsPath) {
        $rootRelativeModelsPath
    } else {
        $installerRelativeModelsPath
    }
}

$mode = if ($Degraded) { "degraded" } else { "full" }
$minimumLlmBytes = 100MB
$minimumEmbeddingBytes = 1MB
$manifestSchemaVersion = 2
$prerequisiteResults = @()
$signingResults = @()

function Reset-Directory([string]$Path) {
    if (Test-Path $Path) {
        Remove-Item -Recurse -Force $Path
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Ensure-Directory([string]$Path) {
    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Get-RequiredFile([string]$Directory, [string[]]$Names, [string]$Label) {
    foreach ($name in $Names) {
        $candidate = Join-Path $Directory $name
        if (Test-Path $candidate -PathType Leaf) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw "$Label not found. Checked: $($Names -join ', ') in $Directory"
}

function Assert-ProductionModel([string]$Path, [int64]$MinimumBytes, [string]$Label) {
    $item = Get-Item $Path
    if ($item.Length -le 0) {
        throw "$Label is empty: $Path"
    }

    if (-not $AllowTestModels -and $item.Length -lt $MinimumBytes) {
        throw "$Label is too small for production packaging ($($item.Length) bytes). Use -BuildProfile NonProduction -AllowTestModels only for CI/dev validation."
    }
}

function Assert-StrongSecret([string]$Value, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Label is required."
    }

    $bytes = [System.Text.Encoding]::UTF8.GetByteCount($Value.Trim())
    $distinct = ($Value.Trim().ToCharArray() | Select-Object -Unique).Count
    $classes = 0
    if ($Value -cmatch '[a-z]') { $classes++ }
    if ($Value -cmatch '[A-Z]') { $classes++ }
    if ($Value -match '\d') { $classes++ }
    if ($Value -match '[^a-zA-Z0-9]') { $classes++ }
    $placeholderFragments = @("change", "placeholder", "secret", "password", "test-key", "dev-local", "poseidon_dev")

    if ($bytes -lt 32 -or $distinct -lt 12 -or $classes -lt 3) {
        throw "$Label must contain at least 32 bytes of high-entropy key material."
    }

    $normalized = $Value.Trim().ToLowerInvariant()
    foreach ($fragment in $placeholderFragments) {
        if ($normalized.Contains($fragment)) {
            throw "$Label contains a placeholder or development value."
        }
    }
}

function New-ModelEntry([string]$SourcePath, [string]$Type, [bool]$Required) {
    $item = Get-Item $SourcePath
    $hash = (Get-FileHash $SourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $target = if ($Type -eq "llm") {
        "[INSTALLDIR]Models\$($item.Name)"
    } else {
        "[INSTALLDIR]Models\arabert.onnx"
    }

    [pscustomobject]@{
        filename = $item.Name
        type = $Type
        required = $Required
        mode = $mode
        sizeBytes = $item.Length
        sha256 = $hash
        targetPath = $target
    }
}

function New-ModelProvenanceEntry([string]$SourcePath, [string]$Type) {
    $item = Get-Item $SourcePath
    [pscustomobject]@{
        filename = $item.Name
        type = $Type
        sourcePath = $item.FullName
        sourceSha256 = (Get-FileHash $SourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        sourceSizeBytes = $item.Length
    }
}

function Write-MachineConfig([object[]]$ModelEntries) {
    $llm = $ModelEntries | Where-Object { $_.type -eq "llm" } | Select-Object -First 1
    $embedding = $ModelEntries | Where-Object { $_.type -eq "embedding" } | Select-Object -First 1

    if ($null -eq $embedding) {
        throw "Embedding model entry is required."
    }

    $encryptionSecretRef = "dpapi:$($SecretScope):Poseidon/EncryptionPassphrase:$SecretKeyVersion"
    $config = [ordered]@{
        Domain = [ordered]@{
            ActiveModule = "legal"
        }
        Instance = [ordered]@{
            Environment = if ($BuildProfile -eq "Production") { "Production" } else { "Development" }
            InstallerMode = $mode
            BuildProfile = $BuildProfile
        }
        Llm = [ordered]@{
            Provider = if ($null -eq $llm) { "ollama" } else { "llamasharp" }
            ModelPath = if ($null -eq $llm) { "" } else { $llm.targetPath }
        }
        Embedding = [ordered]@{
            Provider = "onnx"
            OnnxModelPath = $embedding.targetPath
            Model = "nomic-embed-text"
        }
        Ollama = [ordered]@{
            Url = "http://localhost:11434"
            Model = "qwen2.5:14b"
        }
        Retrieval = [ordered]@{
            StrictMode = $true
            EnableDualPassValidation = $true
        }
        SecretStorage = [ordered]@{
            Provider = "dpapi"
            Scope = $SecretScope
            ValidationMode = if ($BuildProfile -eq "Production") { "Required" } else { "DevelopmentPlaintext" }
        }
        Security = [ordered]@{
            EncryptionEnabled = $true
            EncryptionPassphrase = if ($BuildProfile -eq "Production") { "" } else { $EncryptionPassphrase }
            EncryptionPassphraseRef = if ($BuildProfile -eq "Production") { $encryptionSecretRef } else { "" }
            AllowUnencryptedStorage = $false
            AllowInsecureDevelopmentSecrets = if ($BuildProfile -eq "Production") { $false } else { $true }
            RequireSignedManagementRequests = $true
            EncryptionKeyVersion = $SecretKeyVersion
        }
        ModelIntegrity = [ordered]@{
            ExpectedLlmHash = if ($null -eq $llm) { "" } else { $llm.sha256 }
            ExpectedEmbeddingHash = $embedding.sha256
        }
    }

    $config | ConvertTo-Json -Depth 8 | Set-Content -Path $MachineConfigPath -Encoding UTF8
}

function Write-Manifest([object[]]$ModelEntries) {
    $types = @($ModelEntries | ForEach-Object { $_.type })
    if ($mode -eq "full" -and (@($types | Where-Object { $_ -eq "llm" }).Count -ne 1 -or @($types | Where-Object { $_ -eq "embedding" }).Count -ne 1)) {
        throw "Full installer manifest must contain exactly one LLM and one embedding model."
    }

    if ($mode -eq "degraded" -and (@($types | Where-Object { $_ -eq "llm" }).Count -ne 0 -or @($types | Where-Object { $_ -eq "embedding" }).Count -ne 1)) {
        throw "Degraded installer manifest must contain exactly one embedding model and no local LLM model."
    }

    $manifest = [ordered]@{
        schemaVersion = $manifestSchemaVersion
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        mode = $mode
        packaging = "wix-burn"
        buildProfile = $BuildProfile
        models = $ModelEntries
    }

    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $ModelManifestPath -Encoding UTF8
}

function Write-BuildProvenance([object[]]$ModelProvenanceEntries) {
    $provenance = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        buildProfile = $BuildProfile
        configuration = $Configuration
        mode = $mode
        gitCommit = (& git -C $Root rev-parse HEAD 2>$null)
        models = $ModelProvenanceEntries
        prerequisites = $prerequisiteResults
        signing = $signingResults
        secretStorage = [ordered]@{
            provider = "dpapi"
            scope = $SecretScope
            keyVersion = $SecretKeyVersion
            productionPlaintextSecrets = $false
        }
    }

    $provenance | ConvertTo-Json -Depth 8 | Set-Content -Path $BuildProvenancePath -Encoding UTF8
}

function Copy-StagedModels([object[]]$ModelEntries, [object[]]$ModelSources) {
    $stagedModelsDir = Join-Path $StagedInstallDir "Models"
    Ensure-Directory $stagedModelsDir
    foreach ($entry in $ModelEntries) {
        $source = $ModelSources | Where-Object { $_.type -eq $entry.type -and $_.filename -eq $entry.filename } | Select-Object -First 1
        if ($null -eq $source) {
            throw "Missing source for staged model: $($entry.filename)"
        }

        $targetName = if ($entry.type -eq "embedding") { "arabert.onnx" } else { $entry.filename }
        Copy-Item -Path $source.sourcePath -Destination (Join-Path $stagedModelsDir $targetName) -Force
    }
}

function Get-SignToolPath {
    if (-not [string]::IsNullOrWhiteSpace($SignToolPath) -and (Test-Path $SignToolPath -PathType Leaf)) {
        return (Resolve-Path $SignToolPath).Path
    }

    $sdkRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path $_) }

    foreach ($root in $sdkRoots) {
        $candidate = Get-ChildItem -Path $root -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like "*\x64\signtool.exe" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    return ""
}

function Invoke-ArtifactSigning([string[]]$Artifacts) {
    $requiresSigning = $BuildProfile -eq "Production"
    $signTool = Get-SignToolPath
    if ([string]::IsNullOrWhiteSpace($signTool)) {
        if ($requiresSigning) {
            throw "signtool.exe was not found. Production builds must sign release artifacts."
        }

        if (-not $UnsignedDevelopmentBuild) {
            throw "Unsigned non-production builds require -UnsignedDevelopmentBuild."
        }

        foreach ($artifact in $Artifacts) {
            $script:signingResults += [pscustomobject]@{
                path = $artifact
                status = "unsigned-nonproduction"
                signer = ""
                timestampUrl = ""
            }
        }
        return
    }

    if ([string]::IsNullOrWhiteSpace($SigningCertificateThumbprint) -and [string]::IsNullOrWhiteSpace($SigningCertificatePath)) {
        if ($requiresSigning) {
            throw "Production signing requires -SigningCertificateThumbprint or -SigningCertificatePath."
        }

        if (-not $UnsignedDevelopmentBuild) {
            throw "Unsigned non-production builds require -UnsignedDevelopmentBuild."
        }

        foreach ($artifact in $Artifacts) {
            $script:signingResults += [pscustomobject]@{
                path = $artifact
                status = "unsigned-nonproduction"
                signer = ""
                timestampUrl = ""
            }
        }
        return
    }

    foreach ($artifact in $Artifacts) {
        $args = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256")
        if (-not [string]::IsNullOrWhiteSpace($SigningCertificatePath)) {
            $args += @("/f", $SigningCertificatePath)
            if (-not [string]::IsNullOrWhiteSpace($SigningCertificatePassword)) {
                $args += @("/p", $SigningCertificatePassword)
            }
        } else {
            $args += @("/sha1", $SigningCertificateThumbprint)
        }
        $args += $artifact

        & $signTool @args
        if ($LASTEXITCODE -ne 0) {
            throw "Artifact signing failed: $artifact"
        }

        $signature = Get-AuthenticodeSignature $artifact
        if ($signature.Status -ne "Valid") {
            throw "Signed artifact failed Authenticode verification: $artifact ($($signature.Status))"
        }

        $script:signingResults += [pscustomobject]@{
            path = $artifact
            status = $signature.Status.ToString()
            signer = $signature.SignerCertificate.Subject
            thumbprint = $signature.SignerCertificate.Thumbprint
            timestampUrl = $TimestampUrl
        }
    }
}

function Test-PrerequisitePayloads {
    if (-not (Test-Path $PrerequisitesConfigPath -PathType Leaf)) {
        throw "Prerequisites config missing: $PrerequisitesConfigPath"
    }

    $config = Get-Content -Raw $PrerequisitesConfigPath | ConvertFrom-Json
    if (-not $config.prerequisites -or $config.prerequisites.Count -lt 1) {
        throw "Prerequisites config must contain prerequisite entries."
    }

    Ensure-Directory $PrereqsDir
    foreach ($pr in $config.prerequisites) {
        foreach ($property in @("name", "fileName", "url", "sha256")) {
            if (-not $pr.PSObject.Properties[$property] -or [string]::IsNullOrWhiteSpace([string]$pr.$property)) {
                throw "Prerequisite entry missing required property '$property'."
            }
        }

        if ($pr.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "Prerequisite '$($pr.name)' has invalid SHA-256."
        }

        if ($pr.url -like "https://aka.ms/*" -or $pr.url -like "http://aka.ms/*") {
            throw "Prerequisite '$($pr.name)' uses mutable aka.ms URL. Resolve and pin the immutable payload URL plus SHA-256."
        }

        $target = Join-Path $PrereqsDir $pr.fileName
        if (-not (Test-Path $target) -and -not $SkipPrereqDownload) {
            Invoke-WebRequest -Uri $pr.url -OutFile $target
        }

        if (-not (Test-Path $target) -or (Get-Item $target).Length -le 0) {
            throw "Missing prerequisite payload: $target"
        }

        $actual = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
        $expected = ([string]$pr.sha256).ToLowerInvariant()
        if ($actual -ne $expected) {
            throw "Prerequisite hash mismatch for '$($pr.name)'. Expected $expected, actual $actual."
        }

        $script:prerequisiteResults += [pscustomobject]@{
            name = $pr.name
            fileName = $pr.fileName
            url = $pr.url
            sha256 = $actual
            verified = $true
        }
    }

    $script:prerequisiteResults | ConvertTo-Json -Depth 8 | Set-Content -Path $PrerequisiteReportPath -Encoding UTF8
}

function Assert-ProductionPolicy([object[]]$ModelEntries) {
    if ($BuildProfile -ne "Production") {
        return
    }

    if ($mode -eq "full" -and @($ModelEntries | Where-Object { $_.type -eq "llm" }).Count -ne 1) {
        throw "Production full mode requires a bundled LLM model."
    }

    if (@($ModelEntries | Where-Object { $_.type -eq "embedding" }).Count -ne 1) {
        throw "Production installer requires exactly one embedding model."
    }

    foreach ($entry in $ModelEntries) {
        if ($entry.sha256 -notmatch '^[a-f0-9]{64}$') {
            throw "Production model entry missing required SHA-256: $($entry.filename)"
        }
    }
}

Write-Host ""
Write-Host "===============================================" -ForegroundColor Cyan
Write-Host "  Poseidon Authoritative WiX/Burn Build" -ForegroundColor Cyan
Write-Host "  Configuration : $Configuration" -ForegroundColor Cyan
Write-Host "  Build profile : $BuildProfile" -ForegroundColor Cyan
Write-Host "  Mode          : $mode" -ForegroundColor Cyan
Write-Host "  Models path   : $ModelsPath" -ForegroundColor Cyan
Write-Host "===============================================" -ForegroundColor Cyan
Write-Host ""

Ensure-Directory $InstallerDir
Reset-Directory $OutputDir
Reset-Directory $GeneratedDir
if (Test-Path $ObjDir) {
    Remove-Item -Recurse -Force $ObjDir
}

if (-not $SkipPublish) {
    Reset-Directory $PublishDir
    Reset-Directory $ProvisioningPublishDir

    dotnet publish $DesktopProj -c $Configuration -r win-x64 --self-contained true -o $PublishDir /p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed." }

    dotnet publish $ProvisioningProj -c $Configuration -r win-x64 --self-contained true -o $ProvisioningPublishDir /p:PublishSingleFile=true
    if ($LASTEXITCODE -ne 0) { throw "ProvisioningCheck publish failed." }
}

if (-not (Test-Path (Join-Path $PublishDir "Poseidon.Desktop.exe"))) {
    throw "Poseidon.Desktop.exe missing from publish output: $PublishDir"
}

if (-not (Test-Path $ProvisioningExe)) {
    throw "provisioning-check.exe missing from publish output: $ProvisioningExe"
}

$resolvedModels = Resolve-Path $ModelsPath -ErrorAction SilentlyContinue
if (-not $resolvedModels) {
    throw "Models directory not found: $ModelsPath"
}
$ModelsPath = $resolvedModels.Path

$llmNames = @(
    "qwen2.5-14b.Q5_K_M.gguf",
    "Qwen_Qwen3.5-9B-Q5_K_M.gguf",
    "Qwen3.5-9B-Q5_K_M.gguf"
)

$entries = @()
$provenanceEntries = @()
$llmModelFileName = ""
if (-not $Degraded) {
    $llmPath = Get-RequiredFile -Directory $ModelsPath -Names $llmNames -Label "LLM model"
    Assert-ProductionModel -Path $llmPath -MinimumBytes $minimumLlmBytes -Label "LLM model"
    $llmModelFileName = Split-Path $llmPath -Leaf
    $entries += New-ModelEntry -SourcePath $llmPath -Type "llm" -Required $true
    $provenanceEntries += New-ModelProvenanceEntry -SourcePath $llmPath -Type "llm"
}

$embeddingPath = Get-RequiredFile -Directory $ModelsPath -Names @("arabert.onnx") -Label "Embedding model"
Assert-ProductionModel -Path $embeddingPath -MinimumBytes $minimumEmbeddingBytes -Label "Embedding model"
$entries += New-ModelEntry -SourcePath $embeddingPath -Type "embedding" -Required $true
$provenanceEntries += New-ModelProvenanceEntry -SourcePath $embeddingPath -Type "embedding"

Assert-ProductionPolicy -ModelEntries $entries
Test-PrerequisitePayloads
Write-Manifest -ModelEntries $entries
Write-MachineConfig -ModelEntries $entries
Copy-StagedModels -ModelEntries $entries -ModelSources $provenanceEntries
Write-BuildProvenance -ModelProvenanceEntries $provenanceEntries

Invoke-ArtifactSigning -Artifacts @($ProvisioningExe)
$signingResults | ConvertTo-Json -Depth 8 | Set-Content -Path $SigningReportPath -Encoding UTF8

$checkArgs = @(
    "--config", $MachineConfigPath,
    "--manifest", $ModelManifestPath,
    "--mode", $mode,
    "--install-dir", $StagedInstallDir,
    "--allow-deferred-secrets", "true",
    "--log", (Join-Path $GeneratedDir "provisioning-check-build.log")
)
& $ProvisioningExe @checkArgs
if ($LASTEXITCODE -ne 0) {
    throw "Staged provisioning validation failed."
}

dotnet restore (Join-Path $InstallerDir "Poseidon.Installer.wixproj") --force
if ($LASTEXITCODE -ne 0) { throw "WiX MSI restore failed." }
dotnet restore (Join-Path $InstallerDir "Poseidon.Bundle.wixproj") --force
if ($LASTEXITCODE -ne 0) { throw "WiX bundle restore failed." }

$wixProps = @(
    "/p:ModelsPath=$ModelsPath",
    "/p:LlmModelFileName=$llmModelFileName",
    "/p:ProvisioningCheckPath=$ProvisioningExe",
    "/p:MachineConfigPath=$MachineConfigPath",
    "/p:ModelManifestPath=$ModelManifestPath",
    "/p:InstallerMode=$mode"
)

dotnet build (Join-Path $InstallerDir "Poseidon.Installer.wixproj") -t:Rebuild -c $Configuration -o $OutputDir @wixProps
if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed." }

dotnet build (Join-Path $InstallerDir "Poseidon.Bundle.wixproj") -t:Rebuild -c $Configuration -o $OutputDir @wixProps
if ($LASTEXITCODE -ne 0) { throw "WiX Burn bundle build failed." }

$msi = Get-ChildItem -Path $OutputDir -Filter "*.msi" -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$bundle = Get-ChildItem -Path $OutputDir -Filter "*.exe" -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($null -eq $msi) { throw "MSI artifact was not produced." }
if ($null -eq $bundle) { throw "Burn bundle artifact was not produced." }

$officialMsi = Join-Path $OutputDir "Poseidon.Installer.msi"
$officialBundle = Join-Path $OutputDir "Poseidon.Bundle.exe"
if (-not $msi.FullName.Equals($officialMsi, [StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item $msi.FullName $officialMsi -Force
}
if (-not $bundle.FullName.Equals($officialBundle, [StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item $bundle.FullName $officialBundle -Force
}

Invoke-ArtifactSigning -Artifacts @($officialMsi, $officialBundle)
$signingResults | ConvertTo-Json -Depth 8 | Set-Content -Path $SigningReportPath -Encoding UTF8
Write-BuildProvenance -ModelProvenanceEntries $provenanceEntries

Copy-Item $ModelManifestPath (Join-Path $OutputDir "model-manifest.json") -Force
Copy-Item $BuildProvenancePath (Join-Path $OutputDir "build-provenance.json") -Force
Copy-Item $PrerequisiteReportPath (Join-Path $OutputDir "prerequisite-validation.json") -Force
Copy-Item $SigningReportPath (Join-Path $OutputDir "signing-report.json") -Force

Write-Host ""
Write-Host "===============================================" -ForegroundColor Green
Write-Host "  Installer build completed" -ForegroundColor Green
Write-Host "  MSI    : $officialMsi" -ForegroundColor Green
Write-Host "  Bundle : $officialBundle" -ForegroundColor Green
Write-Host "  Mode   : $mode" -ForegroundColor Green
Write-Host "  Profile: $BuildProfile" -ForegroundColor Green
Write-Host "===============================================" -ForegroundColor Green
