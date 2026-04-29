[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Push-Location $root

try {
    function Get-TrackedFiles {
        param(
            [string[]]$Patterns
        )

        $files = git ls-files --cached --others --exclude-standard -- $Patterns
        if ($LASTEXITCODE -ne 0) {
            throw "git ls-files failed while evaluating repository hygiene."
        }

        return $files | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    }

    $expectedSdk = (Get-Content -Raw "dotnet_version.txt").Trim()
    $globalJson = Get-Content -Raw "global.json" | ConvertFrom-Json
    if ($globalJson.sdk.version -ne $expectedSdk) {
        throw "SDK drift: global.json=$($globalJson.sdk.version), dotnet_version.txt=$expectedSdk"
    }

    $projectFiles = Get-TrackedFiles -Patterns @("*.csproj", "*.wixproj")
    $floatingPackages = $projectFiles | Select-String -Pattern 'Version="[^"]*(\*|latest|\.x)[^"]*"'

    if ($floatingPackages) {
        $details = ($floatingPackages | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }) -join [Environment]::NewLine
        throw "Floating package versions are not allowed:$([Environment]::NewLine)$details"
    }

    $workflowFiles = Get-TrackedFiles -Patterns @(".github/workflows/*.yml", ".github/workflows/*.yaml")
    $floatingSdk = $workflowFiles | Select-String -Pattern "dotnet-version:\s*['""]8\.0\.x['""]" -ErrorAction SilentlyContinue
    if ($floatingSdk) {
        $details = ($floatingSdk | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }) -join [Environment]::NewLine
        throw "CI must use the pinned SDK version ${expectedSdk}:$([Environment]::NewLine)$details"
    }

    $containerFiles = Get-TrackedFiles -Patterns @("*.yml", "*.yaml", "Dockerfile", "**/Dockerfile")
    $dockerLatest = $containerFiles | Select-String -Pattern ":[Ll]atest\b"
    if ($dockerLatest) {
        $details = ($dockerLatest | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }) -join [Environment]::NewLine
        throw "Docker latest tags are not allowed:$([Environment]::NewLine)$details"
    }

    $prerequisitesPath = "installer/prerequisites.json"
    if (Test-Path $prerequisitesPath -PathType Leaf) {
        $prerequisites = Get-Content -Raw $prerequisitesPath | ConvertFrom-Json
        foreach ($prerequisite in $prerequisites.prerequisites) {
            if ([string]$prerequisite.url -match '^https?://aka\.ms/') {
                throw "Prerequisite '$($prerequisite.name)' uses mutable aka.ms URL. Pin the resolved payload URL and SHA-256."
            }

            if ([string]$prerequisite.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
                throw "Prerequisite '$($prerequisite.name)' must declare a SHA-256 hash."
            }
        }
    }

    $activePaths = @(
        "Poseidon.sln",
        ".github",
        "installer",
        "deploy",
        "src/Poseidon.Api",
        "src/Poseidon.Application",
        "src/Poseidon.Desktop",
        "src/Poseidon.Domain",
        "src/Poseidon.Infrastructure",
        "src/Poseidon.Ingestion",
        "src/Poseidon.Management.Api",
        "src/Poseidon.ProvisioningCheck",
        "src/Poseidon.Retrieval",
        "src/Poseidon.Security",
        "src/Poseidon.WorkerService",
        "tests/Poseidon.UnitTests",
        "tests/Poseidon.IntegrationTests"
    )

    $activeFiles = Get-TrackedFiles -Patterns $activePaths

    $legacyRefs = $activeFiles |
        Select-String -Pattern "LegalAI|LegalAI\.sln|Collect-LegalAI|LegalAI\.Bundle|LegalAI\.Installer" -ErrorAction SilentlyContinue
    if ($legacyRefs) {
        $details = ($legacyRefs | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }) -join [Environment]::NewLine
        throw "Active legacy references are not allowed:$([Environment]::NewLine)$details"
    }

    $unsafeSetup = Select-String -Path "setup.ps1" -Pattern "dotnet\s+new|dotnet\s+sln\s+add|--force" -ErrorAction SilentlyContinue
    if ($unsafeSetup) {
        $details = ($unsafeSetup | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }) -join [Environment]::NewLine
        throw "setup.ps1 must remain non-destructive:$([Environment]::NewLine)$details"
    }

    Write-Host "Repository hygiene validation passed."
}
finally {
    Pop-Location
}
