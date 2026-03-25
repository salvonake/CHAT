function Assert-DotNetSdkAvailable {
    $localDotnetDir = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
    $localDotnetExe = Join-Path $localDotnetDir "dotnet.exe"

    function Use-LocalDotnetPath {
        param(
            [string]$PathValue
        )

        if (-not (Test-Path $localDotnetExe)) {
            return
        }

        $parts = @()
        if (-not [string]::IsNullOrWhiteSpace($PathValue)) {
            $parts = $PathValue -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne $localDotnetDir }
        }

        $env:PATH = "$localDotnetDir;" + ($parts -join ';')
    }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Use-LocalDotnetPath -PathValue $env:PATH
        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue

        if (-not $dotnet) {
            throw "dotnet CLI was not found in PATH. Install .NET SDK 8.x and ensure 'dotnet --version' works."
        }
    }

    try {
        & dotnet --version *> $null
    }
    catch {
    }
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path $localDotnetExe) {
            Use-LocalDotnetPath -PathValue $env:PATH
            try {
                & dotnet --version *> $null
            }
            catch {
            }
        }

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet CLI is installed but no compatible SDK is available. Install .NET SDK 8.x and retry."
        }
    }
}

function Remove-StaleTestResultFiles {
    param(
        [string[]]$Patterns
    )

    if (-not $Patterns -or $Patterns.Count -eq 0) {
        return
    }

    if (-not (Test-Path "TestResults")) {
        return
    }

    foreach ($pattern in $Patterns) {
        Get-ChildItem -Path "TestResults" -Filter $pattern -Recurse -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

function Resolve-UnitCoverageMin {
    param(
        [double]$Current,
        [hashtable]$BoundParameters
    )

    if ($BoundParameters -and $BoundParameters.ContainsKey("CoverageMin")) {
        return $Current
    }

    $configured = $env:LEGALAI_UNIT_COVERAGE_MIN
    if ([string]::IsNullOrWhiteSpace($configured)) {
        return $Current
    }

    $value = 0.0
    if ([double]::TryParse($configured, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$value)) {
        return $value
    }

    if ([double]::TryParse($configured, [ref]$value)) {
        return $value
    }

    Write-Warning "Ignoring invalid LEGALAI_UNIT_COVERAGE_MIN value '$configured'. Using $Current."
    return $Current
}

function Get-CoberturaCoverageSummary {
    param(
        [string]$ResultsDirectory = "TestResults"
    )

    $files = Get-ChildItem -Path $ResultsDirectory -Filter coverage.cobertura.xml -Recurse -ErrorAction SilentlyContinue
    if (-not $files -or $files.Count -eq 0) {
        return [pscustomobject]@{
            HasCoverage = $false
            Files = @()
            CoveredLines = 0.0
            ValidLines = 0.0
            CoveragePercent = 0.0
        }
    }

    [double]$covered = 0
    [double]$valid = 0
    foreach ($file in $files) {
        [xml]$coverageXml = Get-Content $file.FullName
        $covered += [double]$coverageXml.coverage.'lines-covered'
        $valid += [double]$coverageXml.coverage.'lines-valid'
    }

    $percent = if ($valid -gt 0) { [math]::Round(($covered / $valid) * 100, 2) } else { 0.0 }

    return [pscustomobject]@{
        HasCoverage = $true
        Files = $files
        CoveredLines = $covered
        ValidLines = $valid
        CoveragePercent = $percent
    }
}
