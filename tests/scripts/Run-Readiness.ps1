param(
    [switch]$Release,
    [switch]$NoBuild,
    [switch]$StartQdrant,
    [string]$OnnxModelPath = "",
    [ValidateRange(0, 100)]
    [double]$CoverageMin = 30,
    [string]$UnitFilter = "",
    [switch]$EnforceCoverageOnFilteredRun
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Common.ps1")

Push-Location (Join-Path $PSScriptRoot "..\..")
try {
    $CoverageMin = Resolve-UnitCoverageMin -Current $CoverageMin -BoundParameters $PSBoundParameters

    $allParams = @{
        Release = $Release
        NoBuild = $NoBuild
        IncludeIntegration = $true
        CollectUnitCoverage = $true
        CoverageMin = $CoverageMin
        StartQdrant = $StartQdrant
    }

    if (-not [string]::IsNullOrWhiteSpace($OnnxModelPath)) {
        $allParams.OnnxModelPath = $OnnxModelPath
    }

    if (-not [string]::IsNullOrWhiteSpace($UnitFilter)) {
        $allParams.UnitFilter = $UnitFilter
    }

    if ($EnforceCoverageOnFilteredRun) {
        $allParams.EnforceCoverageOnFilteredRun = $true
    }

    & ./tests/scripts/Run-AllTests.ps1 @allParams

    if ($LASTEXITCODE -ne 0) {
        throw "Readiness run failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}
