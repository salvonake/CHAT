#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDirectory = ".artifacts\release-evidence\compliance",
    [string]$InstallerOutputDirectory = "installer\output",
    [string]$ReleaseVersion = "",
    [ValidateSet("Production", "NonProduction")]
    [string]$BuildProfile = "Production",
    [switch]$AllowIncomplete
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

    $commit = (git rev-parse HEAD).Trim()
    $branch = (git branch --show-current).Trim()
    $blockers = New-Object System.Collections.Generic.List[string]

    function Add-Blocker([string]$Message) {
        $script:blockers.Add($Message)
    }

    $projects = @(git ls-files -- "*.csproj" "*.wixproj")
    $dependencyInventory = foreach ($project in $projects | Sort-Object) {
        if ($project -like "*.csproj") {
            $packageLines = @(Select-String -Path $project -Pattern '<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"' -AllMatches)
            foreach ($line in $packageLines) {
                foreach ($match in $line.Matches) {
                    [pscustomobject]@{
                        project = $project
                        package = $match.Groups[1].Value
                        version = $match.Groups[2].Value
                        type = "nuget"
                    }
                }
            }
        }
        elseif ($project -like "*.wixproj") {
            [pscustomobject]@{
                project = $project
                package = "WiX project"
                version = ""
                type = "wix"
            }
        }
    }

    $packageLocks = @(git ls-files -- "packages.lock.json" "**/packages.lock.json")
    if ($packageLocks.Count -eq 0) {
        Add-Blocker "No packages.lock.json files are tracked. Locked restore is not yet enforceable."
    }

    $vulnerabilityOutputPath = Join-Path $outputPath "dependency-vulnerability-scan.txt"
    dotnet list Poseidon.sln package --vulnerable --include-transitive > $vulnerabilityOutputPath
    if ($LASTEXITCODE -ne 0) {
        Add-Blocker "dotnet vulnerable package scan failed."
    }

    $vulnerabilityText = Get-Content -Raw $vulnerabilityOutputPath
    $hasVulnerabilities = $vulnerabilityText -match 'has the following vulnerable packages'
    if ($hasVulnerabilities) {
        Add-Blocker "Vulnerable NuGet packages were reported."
    }

    $installerOutputPath = if ([System.IO.Path]::IsPathRooted($InstallerOutputDirectory)) {
        $InstallerOutputDirectory
    } else {
        Join-Path $root $InstallerOutputDirectory
    }

    $expectedArtifacts = @(
        "Poseidon.Installer.msi",
        "Poseidon.Bundle.exe",
        "Setup.exe",
        "model-manifest.json",
        "build-provenance.json",
        "prerequisite-validation.json",
        "native-backend-validation.json",
        "signing-report.json"
    )

    $artifactChecksums = @()
    foreach ($artifactName in $expectedArtifacts) {
        $artifactPath = Join-Path $installerOutputPath $artifactName
        if (Test-Path $artifactPath -PathType Leaf) {
            $item = Get-Item $artifactPath
            $artifactChecksums += [pscustomobject]@{
                name = $artifactName
                path = $artifactPath
                sizeBytes = $item.Length
                sha256 = (Get-FileHash $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
        else {
            Add-Blocker "Required release artifact missing: $artifactName"
        }
    }

    $modelManifestPath = Join-Path $installerOutputPath "model-manifest.json"
    $payloadModelsPath = Join-Path $installerOutputPath "payload\Models"
    $modelCertification = [ordered]@{
        present = Test-Path $modelManifestPath -PathType Leaf
        schemaVersion = $null
        modelCount = 0
        hashesPresent = $false
        sourcePathLeakage = $false
    }
    $externalPayloadCertification = [ordered]@{
        expected = $false
        payloadDirectory = $payloadModelsPath
        directoryPresent = Test-Path $payloadModelsPath -PathType Container
        manifestPayloadCount = 0
        payloadFileCount = 0
        allManifestPayloadsPresent = $true
        allPayloadHashesMatch = $true
        pathTraversalDetected = $false
        files = @()
    }
    if ($modelCertification.present) {
        $modelManifest = Get-Content -Raw $modelManifestPath | ConvertFrom-Json
        $manifestModels = @($modelManifest.models)
        $modelCertification.schemaVersion = $modelManifest.schemaVersion
        $modelCertification.modelCount = $manifestModels.Count
        $modelCertification.hashesPresent = -not (@($manifestModels | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.sha256) -or $_.sha256 -notmatch '^[a-f0-9]{64}$' }).Count -gt 0)
        $modelCertification.sourcePathLeakage = @($manifestModels | Where-Object { $_.PSObject.Properties["sourcePath"] }).Count -gt 0
        if (-not $modelCertification.hashesPresent) { Add-Blocker "Model manifest contains missing or invalid hashes." }
        if ($modelCertification.sourcePathLeakage) { Add-Blocker "Model manifest leaks source paths." }

        $externalManifestModels = @($manifestModels | Where-Object { $_.type -eq "llm" -and [int64]$_.sizeBytes -ge 2147483648 })
        $payloadFiles = @()
        if ($externalPayloadCertification.directoryPresent) {
            $payloadFiles = @(Get-ChildItem -Path $payloadModelsPath -File)
        }

        $externalPayloadCertification.expected = ($externalManifestModels.Count -gt 0 -or $payloadFiles.Count -gt 0)
        $externalPayloadCertification.manifestPayloadCount = $externalManifestModels.Count
        $externalPayloadCertification.payloadFileCount = $payloadFiles.Count

        if ($externalPayloadCertification.expected -and -not $externalPayloadCertification.directoryPresent) {
            $externalPayloadCertification.allManifestPayloadsPresent = $false
            Add-Blocker "External model payload directory is missing beside Setup.exe."
        }

        foreach ($model in $externalManifestModels) {
            $fileName = [string]$model.filename
            $hasTraversal = [string]::IsNullOrWhiteSpace($fileName) -or
                [System.IO.Path]::IsPathRooted($fileName) -or
                $fileName.Contains("..") -or
                $fileName.Contains("\") -or
                $fileName.Contains("/") -or
                ([System.IO.Path]::GetFileName($fileName) -ne $fileName)

            if ($hasTraversal) {
                $externalPayloadCertification.pathTraversalDetected = $true
                Add-Blocker "External payload manifest filename is not a safe leaf name: $fileName"
                continue
            }

            $payloadPath = Join-Path $payloadModelsPath $fileName
            $payloadPresent = Test-Path $payloadPath -PathType Leaf
            $payloadHash = ""
            $payloadSize = 0L
            $hashMatches = $false
            $sizeMatches = $false

            if ($payloadPresent) {
                $payloadItem = Get-Item $payloadPath
                $payloadSize = $payloadItem.Length
                $payloadHash = (Get-FileHash $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
                $hashMatches = ($payloadHash -eq ([string]$model.sha256).ToLowerInvariant())
                $sizeMatches = ($payloadSize -eq [int64]$model.sizeBytes)

                $artifactChecksums += [pscustomobject]@{
                    name = "payload/Models/$fileName"
                    path = $payloadPath
                    sizeBytes = $payloadSize
                    sha256 = $payloadHash
                }
            }

            if (-not $payloadPresent) {
                $externalPayloadCertification.allManifestPayloadsPresent = $false
                Add-Blocker "External model payload missing: payload/Models/$fileName"
            }
            elseif (-not $hashMatches) {
                $externalPayloadCertification.allPayloadHashesMatch = $false
                Add-Blocker "External model payload hash mismatch: payload/Models/$fileName"
            }
            elseif (-not $sizeMatches) {
                $externalPayloadCertification.allPayloadHashesMatch = $false
                Add-Blocker "External model payload size mismatch: payload/Models/$fileName"
            }

            $externalPayloadCertification.files += [pscustomobject]@{
                name = $fileName
                path = $payloadPath
                present = $payloadPresent
                sizeBytes = $payloadSize
                manifestSizeBytes = [int64]$model.sizeBytes
                sha256 = $payloadHash
                manifestSha256 = ([string]$model.sha256).ToLowerInvariant()
                hashMatches = $hashMatches
                sizeMatches = $sizeMatches
            }
        }
    }

    $prerequisitePath = Join-Path $installerOutputPath "prerequisite-validation.json"
    $prerequisiteCertification = [ordered]@{
        present = Test-Path $prerequisitePath -PathType Leaf
        verifiedCount = 0
        allVerified = $false
    }
    if ($prerequisiteCertification.present) {
        $prereqs = @(Get-Content -Raw $prerequisitePath | ConvertFrom-Json)
        $prerequisiteCertification.verifiedCount = @($prereqs | Where-Object { $_.verified -eq $true }).Count
        $prerequisiteCertification.allVerified = ($prereqs.Count -gt 0 -and $prerequisiteCertification.verifiedCount -eq $prereqs.Count)
        if (-not $prerequisiteCertification.allVerified) { Add-Blocker "Prerequisite validation report is incomplete." }
    }

    $nativeBackendPath = Join-Path $installerOutputPath "native-backend-validation.json"
    $nativeBackendCertification = [ordered]@{
        present = Test-Path $nativeBackendPath -PathType Leaf
        requiredCount = 0
        presentRequiredCount = 0
        allRequiredPresent = $false
    }
    if ($nativeBackendCertification.present) {
        $nativeBackends = @((Get-Content -Raw $nativeBackendPath | ConvertFrom-Json) | ForEach-Object { $_ })
        $nativeBackendCertification.requiredCount = @($nativeBackends | Where-Object { $_.required -eq $true }).Count
        $nativeBackendCertification.presentRequiredCount = @($nativeBackends | Where-Object { $_.required -eq $true -and $_.present -eq $true -and $_.sizeBytes -gt 0 }).Count
        $nativeBackendCertification.allRequiredPresent = ($nativeBackendCertification.requiredCount -gt 0 -and $nativeBackendCertification.requiredCount -eq $nativeBackendCertification.presentRequiredCount)
        if (-not $nativeBackendCertification.allRequiredPresent) { Add-Blocker "Native LLamaSharp backend validation report is incomplete." }
    }

    $signingPath = Join-Path $installerOutputPath "signing-report.json"
    $signingCertification = [ordered]@{
        present = Test-Path $signingPath -PathType Leaf
        validSignedArtifacts = 0
        allProductionArtifactsSigned = $false
    }
    if ($signingCertification.present) {
        $signing = @(Get-Content -Raw $signingPath | ConvertFrom-Json)
        $signingCertification.validSignedArtifacts = @($signing | Where-Object { $_.status -eq "Valid" }).Count
        $signingCertification.allProductionArtifactsSigned = ($signing.Count -ge 3 -and $signingCertification.validSignedArtifacts -eq $signing.Count)
        if ($BuildProfile -eq "Production" -and -not $signingCertification.allProductionArtifactsSigned) {
            Add-Blocker "Production signing report does not prove all artifacts are signed."
        }
    }

    $sbom = [ordered]@{
        bomFormat = "Poseidon-SBOM"
        specVersion = "1.0"
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        releaseVersion = $ReleaseVersion
        gitCommit = $commit
        components = @($dependencyInventory)
    }

    $compliance = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        releaseVersion = $ReleaseVersion
        buildProfile = $BuildProfile
        gitCommit = $commit
        gitBranch = $branch
        packageLockFiles = $packageLocks
        vulnerabilityScan = [ordered]@{
            report = $vulnerabilityOutputPath
            vulnerablePackagesReported = $hasVulnerabilities
        }
        modelManifest = $modelCertification
        externalPayload = $externalPayloadCertification
        prerequisites = $prerequisiteCertification
        nativeBackends = $nativeBackendCertification
        signing = $signingCertification
        artifactChecksums = @($artifactChecksums)
        blockers = @($blockers)
        compliant = ($blockers.Count -eq 0)
    }

    $sbom | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $outputPath "sbom.json") -Encoding UTF8
    @($dependencyInventory) | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $outputPath "dependency-inventory.json") -Encoding UTF8
    @($artifactChecksums) | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $outputPath "artifact-checksums.json") -Encoding UTF8
    $compliance | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $outputPath "compliance-report.json") -Encoding UTF8

    if (-not $AllowIncomplete -and $blockers.Count -gt 0) {
        throw "Compliance package generation found blockers: $($blockers -join '; ')"
    }

    Write-Host "Compliance package generated: $outputPath"
}
finally {
    Pop-Location
}
