#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the LegalAI Desktop MSI + EXE installer.

.DESCRIPTION
    1. Publishes the WPF Desktop app as self-contained win-x64
    2. Stages model files (GGUF + ONNX)
    3. Builds WiX v5 MSI installer (Package)
    4. Builds WiX v5 EXE bootstrapper (Burn Bundle)

.PARAMETER ModelsPath
    Path to directory containing model files:
      - qwen2.5-14b.Q5_K_M.gguf  (~10 GB)
      - arabert.onnx               (~500 MB)

.PARAMETER SkipModels
    Skip model bundling (for dev/CI builds without the large model)

.PARAMETER SkipPublish
    Skip dotnet publish (reuse previous publish output)

.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Release

.EXAMPLE
    .\build-installer.ps1 -ModelsPath "D:\models"
    .\build-installer.ps1 -SkipModels          # dev build, no models
    .\build-installer.ps1 -SkipPublish          # reuse last publish
#>
[CmdletBinding()]
param(
    [string]$ModelsPath = ".\models",
    [switch]$SkipModels,
    [switch]$SkipPrereqDownload,
    [switch]$SkipPublish,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root        = Split-Path $PSScriptRoot -Parent
$PublishDir  = Join-Path $Root "publish\win-x64"
$InstallerDir = $PSScriptRoot
$OutputDir   = Join-Path $InstallerDir "output"
$PrereqsDir  = Join-Path $InstallerDir "prereqs"
$PlaceholderModelsDir = Join-Path $InstallerDir "models-placeholder"
$DesktopProj = Join-Path $Root "src\LegalAI.Desktop\LegalAI.Desktop.csproj"
$ExternalModelsManifest = Join-Path $OutputDir "external-models.manifest.json"

$modelManifestEntries = @()
$externalModelsMode = $false
$modelSourcePathForManifest = $null

function Initialize-PlaceholderModels {
    param(
        [string]$PlaceholderDir
    )

    if (-not (Test-Path $PlaceholderDir)) {
        New-Item -ItemType Directory -Path $PlaceholderDir -Force | Out-Null
    }

    $placeholderLlm = Join-Path $PlaceholderDir "qwen2.5-14b.Q5_K_M.gguf"
    $placeholderEmb = Join-Path $PlaceholderDir "arabert.onnx"

    if (-not (Test-Path $placeholderLlm)) {
        Set-Content -Path $placeholderLlm -Value "placeholder-llm-model" -Encoding ASCII
    }

    if (-not (Test-Path $placeholderEmb)) {
        Set-Content -Path $placeholderEmb -Value "placeholder-embedding-model" -Encoding ASCII
    }

    return @{
        ModelsPath = $PlaceholderDir
        LlmModelFileName = "qwen2.5-14b.Q5_K_M.gguf"
    }
}

Write-Host ""
Write-Host "===============================================" -ForegroundColor Cyan
Write-Host "  LegalAI Desktop - MSI + EXE Installer Build"  -ForegroundColor Cyan
Write-Host "  Configuration : $Configuration"                -ForegroundColor Cyan
Write-Host "  Models path   : $(if ($SkipModels) { '(skipped)' } else { $ModelsPath })" -ForegroundColor Cyan
Write-Host "===============================================" -ForegroundColor Cyan
Write-Host ""

# ─────────────────────────────────────────
# Step 1: Publish Desktop application
# ─────────────────────────────────────────
if (-not $SkipPublish) {
    Write-Host "[1/6] Publishing LegalAI.Desktop (self-contained win-x64)..." -ForegroundColor Yellow

    if (-not (Test-Path $DesktopProj)) {
        Write-Error "Desktop project not found: $DesktopProj"
        exit 1
    }

    # Clean previous publish
    if (Test-Path $PublishDir) {
        Write-Host "  Cleaning previous publish output..." -ForegroundColor DarkGray
        Remove-Item -Recurse -Force $PublishDir
    }

    dotnet publish $DesktopProj `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -o $PublishDir `
        /p:PublishSingleFile=false `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:DebugType=none `
        /p:DebugSymbols=false

    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
        exit 1
    }

    $fileCount = (Get-ChildItem $PublishDir -Recurse -File).Count
    $totalSize = (Get-ChildItem $PublishDir -Recurse -File | Measure-Object -Sum Length).Sum / 1MB
    Write-Host "  Published: $fileCount files, $([math]::Round($totalSize, 1)) MB" -ForegroundColor Green
}
else {
    Write-Host "[1/6] Skipping publish (-SkipPublish)..." -ForegroundColor DarkGray

    if (-not (Test-Path $PublishDir)) {
        Write-Error "No previous publish output found at $PublishDir. Run without -SkipPublish first."
        exit 1
    }
}

# ─────────────────────────────────────────
# Step 2: Validate model files
# ─────────────────────────────────────────
if (-not $SkipModels) {
    Write-Host "[2/6] Validating model files..." -ForegroundColor Yellow

    $ResolvedModels = Resolve-Path $ModelsPath -ErrorAction SilentlyContinue
    if (-not $ResolvedModels) {
        Write-Error "Models directory not found: $ModelsPath`nUse -ModelsPath <dir> or -SkipModels for dev builds."
        exit 1
    }
    $ModelsPath = $ResolvedModels.Path
    $modelSourcePathForManifest = $ModelsPath

    $llmCandidates = @(
        "qwen2.5-14b.Q5_K_M.gguf",
        "Qwen_Qwen3.5-9B-Q5_K_M.gguf",
        "Qwen3.5-9B-Q5_K_M.gguf"
    )

    $llmModelFileName = $llmCandidates | Where-Object {
        Test-Path (Join-Path $ModelsPath $_)
    } | Select-Object -First 1

    if (-not $llmModelFileName) {
        Write-Warning "Optional LLM model missing. Checked: $($llmCandidates -join ', ')"
    }
    else {
        $llmPath = Join-Path $ModelsPath $llmModelFileName
        $llmSize = (Get-Item $llmPath).Length
        $llmUnit = if ($llmSize -gt 1GB) { "$([math]::Round($llmSize / 1GB, 2)) GB" } else { "$([math]::Round($llmSize / 1MB, 1)) MB" }
        Write-Host "  [OK] $llmModelFileName - $llmUnit" -ForegroundColor Green

        if ($llmSize -ge 2GB) {
            Write-Warning "LLM model '$llmModelFileName' is too large for MSI embedding ($llmUnit). Switching to external-model mode for this build."
            $externalModelsMode = $true
        }

        Write-Host "    Computing SHA-256..." -ForegroundColor DarkGray
        $llmHash = (Get-FileHash $llmPath -Algorithm SHA256).Hash.ToLower()
        Write-Host "    Hash: $llmHash" -ForegroundColor DarkGray

        $modelManifestEntries += [pscustomobject]@{
            Name = $llmModelFileName
            Type = "llm"
            Required = $false
            SourcePath = $llmPath
            SizeBytes = $llmSize
            Sha256 = $llmHash
        }
    }

    $models = @(
        @{ Name = "arabert.onnx"; Required = $true; Feature = "Embedding Model" }
    )

    $allPresent = $true
    foreach ($m in $models) {
        $fp = Join-Path $ModelsPath $m.Name
        if (Test-Path $fp) {
            $sz = (Get-Item $fp).Length
            $unit = if ($sz -gt 1GB) { "$([math]::Round($sz / 1GB, 2)) GB" } else { "$([math]::Round($sz / 1MB, 1)) MB" }
            Write-Host "  [OK] $($m.Name) - $unit" -ForegroundColor Green

            if ($sz -ge 2GB) {
                Write-Warning "Model '$($m.Name)' is too large for MSI embedding ($unit). Switching to external-model mode for this build."
                $externalModelsMode = $true
            }

            # Compute SHA-256 for integrity verification at runtime
            Write-Host "    Computing SHA-256..." -ForegroundColor DarkGray
            $hash = (Get-FileHash $fp -Algorithm SHA256).Hash.ToLower()
            Write-Host "    Hash: $hash" -ForegroundColor DarkGray

            $modelManifestEntries += [pscustomobject]@{
                Name = $m.Name
                Type = "embedding"
                Required = [bool]$m.Required
                SourcePath = $fp
                SizeBytes = $sz
                Sha256 = $hash
            }
        }
        else {
            if ($m.Required) {
                Write-Error "Required model missing: $fp"
                $allPresent = $false
            }
            else {
                Write-Warning "Optional model missing: $fp (LLM feature will be excluded)"
            }
        }
    }

    if (-not $allPresent) {
        Write-Error "Required model files are missing. Build aborted."
        exit 1
    }

    if ($externalModelsMode) {
        Write-Warning "One or more models exceed MSI payload limits. Continuing with external-model packaging mode."
        $placeholderConfig = Initialize-PlaceholderModels -PlaceholderDir $PlaceholderModelsDir
        $ModelsPath = $placeholderConfig.ModelsPath
        $llmModelFileName = $placeholderConfig.LlmModelFileName
    }
}
else {
    Write-Host "[2/6] Skipping model validation (-SkipModels)..." -ForegroundColor DarkGray

    $placeholderConfig = Initialize-PlaceholderModels -PlaceholderDir $PlaceholderModelsDir
    $ModelsPath = $placeholderConfig.ModelsPath
    $llmModelFileName = $placeholderConfig.LlmModelFileName
}

# ─────────────────────────────────────────
# Step 3: Download prerequisite installers
# ─────────────────────────────────────────
Write-Host "[3/6] Preparing prerequisite installers..." -ForegroundColor Yellow

if (-not (Test-Path $PrereqsDir)) {
    New-Item -ItemType Directory -Path $PrereqsDir -Force | Out-Null
}

$prereqs = @(
    @{ Name = "vc_redist.x64.exe"; Url = "https://aka.ms/vs/17/release/vc_redist.x64.exe" },
    @{ Name = "windowsdesktop-runtime-8.0-win-x64.exe"; Url = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe" }
)

if ($SkipPrereqDownload) {
    Write-Host "  Skipping prerequisite download (-SkipPrereqDownload)." -ForegroundColor DarkGray
}
else {
    foreach ($pr in $prereqs) {
        $target = Join-Path $PrereqsDir $pr.Name
        $needsDownload = $true

        if (Test-Path $target) {
            $existingSize = (Get-Item $target).Length
            if ($existingSize -gt 0) {
                $needsDownload = $false
                Write-Host "  [OK] Found $($pr.Name)" -ForegroundColor Green
            }
        }

        if ($needsDownload) {
            Write-Host "  Downloading $($pr.Name)..." -ForegroundColor DarkGray
            try {
                Invoke-WebRequest -Uri $pr.Url -OutFile $target
            }
            catch {
                Write-Error "Failed to download $($pr.Name) from $($pr.Url): $($_.Exception.Message)"
                exit 1
            }

            if (-not (Test-Path $target) -or (Get-Item $target).Length -le 0) {
                Write-Error "Downloaded file is missing or empty: $target"
                exit 1
            }

            Write-Host "  [OK] Downloaded $($pr.Name)" -ForegroundColor Green
        }
    }
}

# Validate prerequisite payloads for bundle build
foreach ($pr in $prereqs) {
    $target = Join-Path $PrereqsDir $pr.Name
    if (-not (Test-Path $target)) {
        Write-Error "Missing prerequisite payload: $target`nRun without -SkipPrereqDownload or place the file manually."
        exit 1
    }
}

# ─────────────────────────────────────────
# Step 4: Verify Resources
# ─────────────────────────────────────────
Write-Host "[4/6] Verifying installer resources..." -ForegroundColor Yellow

$resDir = Join-Path $InstallerDir "Resources"
$icoPath = Join-Path $resDir "LegalAI.ico"
if (-not (Test-Path $icoPath)) {
    Write-Warning "Icon file not found: $icoPath - creating placeholder..."
    if (-not (Test-Path $resDir)) { New-Item -ItemType Directory -Path $resDir -Force | Out-Null }

    # Create a minimal valid .ico file (16x16 1-bit, ~198 bytes)
    # This is a placeholder - replace with actual branding icon
    $icoHeader = [byte[]]@(0,0,1,0,1,0,16,16,2,0,1,0,1,0,0xB0,0,0,0,0x16,0,0,0)
    $bmpHeader = [byte[]]@(
        0x28,0,0,0,  # biSize=40
        16,0,0,0,    # biWidth=16
        32,0,0,0,    # biHeight=32 (XOR+AND)
        1,0,         # biPlanes=1
        1,0,         # biBitCount=1
        0,0,0,0,     # biCompression=0
        0x80,0,0,0,  # biSizeImage=128
        0,0,0,0,     # biXPelsPerMeter
        0,0,0,0,     # biYPelsPerMeter
        2,0,0,0,     # biClrUsed=2
        0,0,0,0      # biClrImportant
    )
    $palette = [byte[]]@(0x1A,0x1A,0x2E,0, 0xD4,0xAF,0x37,0)  # Dark blue + gold
    $xorBits = [byte[]]::new(64)  # 16x16 monochrome XOR mask, all 0 = first color
    $andBits = [byte[]]::new(64)  # AND mask, all 0 = opaque

    $icoData = $icoHeader + $bmpHeader + $palette + $xorBits + $andBits
    [System.IO.File]::WriteAllBytes($icoPath, $icoData)
    Write-Host "  Created placeholder icon (replace with branding icon later)" -ForegroundColor DarkYellow
}
else {
    Write-Host "  [OK] Icon file found" -ForegroundColor Green
}

# ─────────────────────────────────────────
# Step 5: Build WiX MSI
# ─────────────────────────────────────────
Write-Host "[5/6] Building WiX MSI installer..." -ForegroundColor Yellow

# Ensure output directory exists
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$manifestOutput = $null
if ($modelManifestEntries.Count -gt 0) {
    $manifestMode = if ($externalModelsMode -or $SkipModels) { "external" } else { "embedded" }
    $manifest = [pscustomobject]@{
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        mode = $manifestMode
        sourceModelsPath = $modelSourcePathForManifest
        models = $modelManifestEntries
    }

    $manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $ExternalModelsManifest -Encoding UTF8
    $manifestOutput = $ExternalModelsManifest
    Write-Host "  [OK] Model manifest generated: $manifestOutput" -ForegroundColor Green
}

$buildStamp = Get-Date -Format 'yyyyMMdd'
$msiBaseName = "LegalAI-Setup-$buildStamp"
$msiOutput = Join-Path $OutputDir "$msiBaseName.msi"

if (Test-Path $msiOutput -PathType Container) {
    Remove-Item -Recurse -Force $msiOutput
}

# Build WiX Package project (MSI)
$wixBuildArgs = @(
    "build",
    (Join-Path $InstallerDir "LegalAI.Installer.wixproj"),
    "-t:Rebuild",
    "-c", $Configuration,
    "-o", $OutputDir
)

# Pass models path if not skipping
$wixBuildArgs += "/p:ModelsPath=$ModelsPath"
$wixBuildArgs += "/p:LlmModelFileName=$llmModelFileName"

Write-Host "  Running: dotnet $($wixBuildArgs -join ' ')" -ForegroundColor DarkGray

& dotnet @wixBuildArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "WiX MSI build failed with exit code $LASTEXITCODE"
    exit 1
}

if (-not (Test-Path $msiOutput)) {
    $msiCandidate = Get-ChildItem -Path $OutputDir -Filter "*.msi" -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $msiCandidate) {
        Write-Error "MSI output was not found in $OutputDir"
        exit 1
    }
    $msiOutput = $msiCandidate.FullName
}

Write-Host "  [OK] MSI built: $msiOutput" -ForegroundColor Green

# ─────────────────────────────────────────
# Step 6: Build WiX EXE Bootstrapper (Burn)
# ─────────────────────────────────────────
Write-Host "[6/6] Building WiX EXE bootstrapper (Burn Bundle)..." -ForegroundColor Yellow

$exeBaseName = "LegalAI-Setup-$buildStamp"
$exeOutput = Join-Path $OutputDir "$exeBaseName.exe"

if (Test-Path $exeOutput -PathType Container) {
    Remove-Item -Recurse -Force $exeOutput
}

# Build WiX Bundle project (EXE)
$bundleBuildArgs = @(
    "build",
    (Join-Path $InstallerDir "LegalAI.Bundle.wixproj"),
    "-t:Rebuild",
    "-c", $Configuration,
    "-o", $OutputDir
)

$bundleBuildArgs += "/p:ModelsPath=$ModelsPath"
$bundleBuildArgs += "/p:LlmModelFileName=$llmModelFileName"

Write-Host "  Running: dotnet $($bundleBuildArgs -join ' ')" -ForegroundColor DarkGray

& dotnet @bundleBuildArgs

if ($LASTEXITCODE -ne 0) {
    Write-Warning "Burn Bundle build failed (exit code $LASTEXITCODE). MSI is still available."
    $exeOutput = $null
}
else {
    if (-not (Test-Path $exeOutput)) {
        $exeCandidate = Get-ChildItem -Path $OutputDir -Filter "*.exe" -File |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($null -eq $exeCandidate) {
            Write-Warning "EXE output path was not found in $OutputDir."
            $exeOutput = $null
        }
        else {
            $exeOutput = $exeCandidate.FullName
        }
    }
    Write-Host "  [OK] EXE built: $exeOutput" -ForegroundColor Green
}

# ─────────────────────────────────────────
# Done
# ─────────────────────────────────────────
$msiSize = if (Test-Path $msiOutput) {
    $sz = (Get-Item $msiOutput).Length
    if ($sz -gt 1GB) { "$([math]::Round($sz / 1GB, 2)) GB" } else { "$([math]::Round($sz / 1MB, 1)) MB" }
} else { "unknown" }

$exeSize = if ($exeOutput -and (Test-Path $exeOutput)) {
    $sz = (Get-Item $exeOutput).Length
    if ($sz -gt 1GB) { "$([math]::Round($sz / 1GB, 2)) GB" } else { "$([math]::Round($sz / 1MB, 1)) MB" }
} else { "N/A" }

Write-Host ""
Write-Host "===============================================" -ForegroundColor Green
Write-Host "  [OK] Installer build completed!"               -ForegroundColor Green
Write-Host ""
Write-Host "  MSI Output : $msiOutput"                       -ForegroundColor Green
Write-Host "  MSI Size   : $msiSize"                         -ForegroundColor Green
if ($externalModelsMode) {
    Write-Host "  Model Mode : external (not bundled)"       -ForegroundColor Yellow
}
if ($manifestOutput) {
    Write-Host "  Manifest   : $manifestOutput"              -ForegroundColor Green
}
if ($exeOutput -and (Test-Path $exeOutput)) {
    Write-Host "  EXE Output : $exeOutput"                   -ForegroundColor Green
    Write-Host "  EXE Size   : $exeSize"                     -ForegroundColor Green
}
Write-Host ""
Write-Host "  Install (MSI GUI)    : msiexec /i `"$msiOutput`""         -ForegroundColor White
Write-Host "  Install (MSI silent) : msiexec /i `"$msiOutput`" /qn"     -ForegroundColor White
if ($exeOutput) {
    Write-Host "  Install (EXE GUI)    : `"$exeOutput`""                 -ForegroundColor White
    Write-Host "  Install (EXE silent) : `"$exeOutput`" /quiet"          -ForegroundColor White
}
Write-Host "===============================================" -ForegroundColor Green
