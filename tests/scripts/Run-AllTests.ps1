param(
    [switch]$Release,
    [switch]$NoBuild,
    [switch]$IncludeIntegration,
    [switch]$StartQdrant,
    [string]$OnnxModelPath = "",
    [string]$UnitFilter = "",
    [switch]$CollectUnitCoverage,
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

    Write-Host "Running unit test suite..."
    $unitParams = @{ NoBuild = $NoBuild; Release = $Release }
    if (-not [string]::IsNullOrWhiteSpace($UnitFilter)) {
        $unitParams.Filter = $UnitFilter
    }
    if ($CollectUnitCoverage) {
        $unitParams.CollectCoverage = $true
        $unitParams.CoverageMin = $CoverageMin
        if ($EnforceCoverageOnFilteredRun) {
            $unitParams.EnforceCoverageOnFilteredRun = $true
        }
    }

    & ./tests/scripts/Run-UnitTests.ps1 @unitParams

    if ($LASTEXITCODE -ne 0) {
        throw "Unit test suite failed with exit code $LASTEXITCODE"
    }

    if ($IncludeIntegration) {
        Write-Host ""
        Write-Host "Running integration test suite..."
        $integrationParams = @{
            Release = $Release
            NoBuild = $NoBuild
            StartQdrant = $StartQdrant
            OnnxModelPath = $OnnxModelPath
        }

        & ./tests/scripts/Run-IntegrationTests.ps1 @integrationParams

        if ($LASTEXITCODE -ne 0) {
            throw "Integration test suite failed with exit code $LASTEXITCODE"
        }
    }

    $unitTrx = Get-ChildItem -Path TestResults -Filter unit-local.trx -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    $integrationTrx = Get-ChildItem -Path TestResults -Filter integration-local.trx -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    Write-Host ""
    Write-Host "=============================="
    Write-Host "Combined local test summary"
    Write-Host "=============================="

    if ($unitTrx) {
        [xml]$ux = Get-Content $unitTrx.FullName
        $uc = $ux.TestRun.ResultSummary.Counters
        Write-Host "Unit tests"
        Write-Host "- Total : $($uc.total)"
        Write-Host "- Passed: $($uc.passed)"
        Write-Host "- Failed: $($uc.failed)"
        Write-Host "- TRX   : $($unitTrx.FullName)"
    }

    if ($IncludeIntegration) {
        if ($integrationTrx) {
            [xml]$ix = Get-Content $integrationTrx.FullName
            $ic = $ix.TestRun.ResultSummary.Counters
            Write-Host ""
            Write-Host "Integration tests"
            Write-Host "- Total : $($ic.total)"
            Write-Host "- Passed: $($ic.passed)"
            Write-Host "- Failed: $($ic.failed)"
            Write-Host "- TRX   : $($integrationTrx.FullName)"
        }
        else {
            Write-Host ""
            Write-Host "Integration tests"
            Write-Host "- No integration TRX found."
        }
    }

    if ($CollectUnitCoverage) {
        $coverage = Get-CoberturaCoverageSummary -ResultsDirectory "TestResults"

        if ($coverage.HasCoverage) {
            if ($coverage.ValidLines -gt 0) {
                $coveragePercent = $coverage.CoveragePercent
                $hasFilter = -not [string]::IsNullOrWhiteSpace($UnitFilter)
                if ($hasFilter -and -not $EnforceCoverageOnFilteredRun) {
                    $gateStatus = "SKIPPED (filtered run)"
                }
                else {
                    $gateStatus = if ($coveragePercent -ge $CoverageMin) { "PASSED" } else { "FAILED" }
                }
                Write-Host ""
                Write-Host "Unit coverage"
                Write-Host "- Coverage: $coveragePercent%"
                Write-Host "- Minimum : $CoverageMin%"
                Write-Host "- Gate    : $gateStatus"
            }
            else {
                Write-Host ""
                Write-Host "Unit coverage"
                Write-Host "- Coverage report found but contains zero valid lines."
            }
        }
        else {
            Write-Host ""
            Write-Host "Unit coverage"
            Write-Host "- No Cobertura report found in TestResults."
        }
    }
}
finally {
    Pop-Location
}
