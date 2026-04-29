param(
    [string]$QdrantHost = "127.0.0.1",
    [ValidateRange(1, 65535)]
    [int]$QdrantPort = 6334,
    [string]$OnnxModelPath = "",
    [switch]$StartQdrant,
    [switch]$Release,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Common.ps1")

Push-Location (Join-Path $PSScriptRoot "..\..")

$originalEnv = @{
    POSEIDON_RUN_INTEGRATION = [Environment]::GetEnvironmentVariable("POSEIDON_RUN_INTEGRATION", "Process")
    POSEIDON_QDRANT_HOST = [Environment]::GetEnvironmentVariable("POSEIDON_QDRANT_HOST", "Process")
    POSEIDON_QDRANT_PORT = [Environment]::GetEnvironmentVariable("POSEIDON_QDRANT_PORT", "Process")
    POSEIDON_ONNX_MODEL_PATH = [Environment]::GetEnvironmentVariable("POSEIDON_ONNX_MODEL_PATH", "Process")
}

try {
    Assert-DotNetSdkAvailable

    Remove-StaleTestResultFiles -Patterns @("integration-local.trx")

    if ($StartQdrant) {
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            throw "Docker CLI was not found in PATH. Install/start Docker Desktop or run without -StartQdrant if Qdrant is already running."
        }

        Write-Host "Starting Qdrant via docker compose..."
        docker compose -f deploy/docker/docker-compose.yml up -d
    }

    $env:POSEIDON_RUN_INTEGRATION = "true"
    $env:POSEIDON_QDRANT_HOST = $QdrantHost
    $env:POSEIDON_QDRANT_PORT = "$QdrantPort"

    if (-not [string]::IsNullOrWhiteSpace($OnnxModelPath)) {
        if (-not (Test-Path $OnnxModelPath)) {
            throw "ONNX model file not found: $OnnxModelPath"
        }
        $env:POSEIDON_ONNX_MODEL_PATH = $OnnxModelPath
    }

    $configuration = if ($Release) { "Release" } else { "Debug" }
    $integrationAssemblyPath = "tests/Poseidon.IntegrationTests/bin/$configuration/net8.0/Poseidon.IntegrationTests.dll"

    if ($NoBuild -and -not (Test-Path $integrationAssemblyPath)) {
        throw "NoBuild was specified but integration test assembly was not found at '$integrationAssemblyPath'. Run once without -NoBuild first."
    }

    if (-not $NoBuild) {
        dotnet build tests/Poseidon.IntegrationTests/Poseidon.IntegrationTests.csproj --configuration $configuration
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE"
        }
    }

    dotnet test tests/Poseidon.IntegrationTests/Poseidon.IntegrationTests.csproj `
        --no-build --configuration $configuration --verbosity normal `
        --logger "trx;LogFileName=integration-local.trx" --results-directory TestResults
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE"
    }

    $trx = Get-ChildItem -Path TestResults -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($trx) {
        [xml]$xml = Get-Content $trx.FullName
        $c = $xml.TestRun.ResultSummary.Counters
        Write-Host ""
        Write-Host "Integration test summary"
        Write-Host "- Total : $($c.total)"
        Write-Host "- Passed: $($c.passed)"
        Write-Host "- Failed: $($c.failed)"
        Write-Host "- TRX   : $($trx.FullName)"
    }
}
finally {
    foreach ($name in $originalEnv.Keys) {
        [Environment]::SetEnvironmentVariable($name, $originalEnv[$name], "Process")
    }
    Pop-Location
}

