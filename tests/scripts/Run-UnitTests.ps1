param(
    [switch]$Release,
    [switch]$NoBuild,
    [string]$Filter = "",
    [switch]$CollectCoverage,
    [ValidateRange(0, 100)]
    [double]$CoverageMin = 30,
    [switch]$EnforceCoverageOnFilteredRun
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Common.ps1")

Push-Location (Join-Path $PSScriptRoot "..\..")
try {
    Assert-DotNetSdkAvailable
    $CoverageMin = Resolve-UnitCoverageMin -Current $CoverageMin -BoundParameters $PSBoundParameters

    $configuration = if ($Release) { "Release" } else { "Debug" }
    $unitAssemblyPath = "tests/LegalAI.UnitTests/bin/$configuration/net8.0-windows/LegalAI.UnitTests.dll"

    Remove-StaleTestResultFiles -Patterns @("unit-local.trx", "coverage.cobertura.xml")

    if ($NoBuild -and -not (Test-Path $unitAssemblyPath)) {
        throw "NoBuild was specified but unit test assembly was not found at '$unitAssemblyPath'. Run once without -NoBuild first."
    }

    if (-not $NoBuild) {
        dotnet build tests/LegalAI.UnitTests/LegalAI.UnitTests.csproj --configuration $configuration
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE"
        }
    }

    $testArgs = @(
        "test",
        "tests/LegalAI.UnitTests/LegalAI.UnitTests.csproj",
        "--no-build",
        "--configuration", $configuration,
        "--verbosity", "normal",
        "--logger", "trx;LogFileName=unit-local.trx",
        "--results-directory", "TestResults"
    )

    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $testArgs += @("--filter", $Filter)
    }

    if ($CollectCoverage) {
        $testArgs += @("--collect:XPlat Code Coverage")
    }

    dotnet @testArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE"
    }

    $trx = Get-ChildItem -Path TestResults -Filter unit-local.trx -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($trx) {
        [xml]$xml = Get-Content $trx.FullName
        $c = $xml.TestRun.ResultSummary.Counters
        Write-Host ""
        Write-Host "Unit test summary"
        Write-Host "- Total : $($c.total)"
        Write-Host "- Passed: $($c.passed)"
        Write-Host "- Failed: $($c.failed)"
        Write-Host "- TRX   : $($trx.FullName)"
    }

    if ($CollectCoverage) {
        $coverage = Get-CoberturaCoverageSummary -ResultsDirectory "TestResults"
        if (-not $coverage.HasCoverage) {
            throw "Coverage was requested but no Cobertura report was produced."
        }

        if ($coverage.ValidLines -le 0) {
            throw "Coverage report has zero valid lines."
        }

        $coveragePercent = $coverage.CoveragePercent
        $hasFilter = -not [string]::IsNullOrWhiteSpace($Filter)
        $shouldEnforceGate = (-not $hasFilter) -or $EnforceCoverageOnFilteredRun

        Write-Host ""
        Write-Host "Unit coverage summary"
        Write-Host "- Coverage: $coveragePercent%"
        Write-Host "- Minimum : $CoverageMin%"
        if ($shouldEnforceGate) {
            $gateStatus = if ($coveragePercent -ge $CoverageMin) { "PASSED" } else { "FAILED" }
            Write-Host "- Gate    : $gateStatus"
        }
        else {
            Write-Host "- Gate    : SKIPPED (filtered run)"
        }

        if ($shouldEnforceGate -and $coveragePercent -lt $CoverageMin) {
            throw "Coverage gate failed: $coveragePercent% < $CoverageMin%"
        }
    }
}
finally {
    Pop-Location
}
