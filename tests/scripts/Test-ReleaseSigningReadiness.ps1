#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet("Production", "NonProduction")]
    [string]$BuildProfile = "Production",
    [switch]$AllowUnsigned,
    [string]$OutputDirectory = ".artifacts\release-evidence",
    [string]$SignToolPath = "",
    [string]$SigningCertificateThumbprint = "",
    [string]$SigningCertificatePath = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
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

    function Resolve-SignTool {
        if (-not [string]::IsNullOrWhiteSpace($SignToolPath) -and (Test-Path $SignToolPath -PathType Leaf)) {
            return (Resolve-Path $SignToolPath).Path
        }

        $sdkRoots = @(
            "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
            "$env:ProgramFiles\Windows Kits\10\bin"
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path $_) }

        foreach ($sdkRoot in $sdkRoots) {
            $candidate = Get-ChildItem -Path $sdkRoot -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -like "*\x64\signtool.exe" } |
                Sort-Object FullName -Descending |
                Select-Object -First 1
            if ($candidate) {
                return $candidate.FullName
            }
        }

        return ""
    }

    $resolvedSignTool = Resolve-SignTool
    $certificate = $null
    $certificateSource = ""

    if (-not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
        $certificate = Get-ChildItem -Path Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $SigningCertificateThumbprint } |
            Select-Object -First 1
        $certificateSource = "certificate-store"
    }
    elseif (-not [string]::IsNullOrWhiteSpace($SigningCertificatePath)) {
        if (Test-Path $SigningCertificatePath -PathType Leaf) {
            $certificateSource = "pfx-file"
        }
    }

    $report = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        buildProfile = $BuildProfile
        allowUnsigned = [bool]$AllowUnsigned
        signToolPath = $resolvedSignTool
        signToolPresent = -not [string]::IsNullOrWhiteSpace($resolvedSignTool)
        certificateSource = $certificateSource
        certificateRequestedThumbprint = $SigningCertificateThumbprint
        certificatePathProvided = -not [string]::IsNullOrWhiteSpace($SigningCertificatePath)
        timestampUrl = $TimestampUrl
        productionReady = $false
        signerSubject = ""
        signerThumbprint = ""
        signerNotAfterUtc = ""
        blockers = @()
    }

    $blockers = New-Object System.Collections.Generic.List[string]
    if ([string]::IsNullOrWhiteSpace($resolvedSignTool)) {
        $blockers.Add("signtool.exe was not found.")
    }

    if ([string]::IsNullOrWhiteSpace($SigningCertificateThumbprint) -and [string]::IsNullOrWhiteSpace($SigningCertificatePath)) {
        $blockers.Add("No signing certificate thumbprint or PFX path was provided.")
    }

    if (-not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint) -and -not $certificate) {
        $blockers.Add("Signing certificate thumbprint was not found in CurrentUser or LocalMachine certificate stores.")
    }

    if (-not [string]::IsNullOrWhiteSpace($SigningCertificatePath) -and -not (Test-Path $SigningCertificatePath -PathType Leaf)) {
        $blockers.Add("Signing certificate PFX path does not exist.")
    }

    if ($certificate) {
        $report.signerSubject = $certificate.Subject
        $report.signerThumbprint = $certificate.Thumbprint
        $report.signerNotAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString("o")
    }

    $report.blockers = @($blockers)
    $report.productionReady = ($blockers.Count -eq 0)
    $report | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $outputPath "signing-readiness-report.json") -Encoding UTF8

    if ($BuildProfile -eq "Production" -and $blockers.Count -gt 0) {
        throw "Production signing readiness failed: $($blockers -join '; ')"
    }

    if (-not $AllowUnsigned -and $BuildProfile -eq "NonProduction" -and $blockers.Count -gt 0) {
        throw "Signing readiness failed. Use -AllowUnsigned only for explicit non-production validation."
    }

    Write-Host "Signing readiness report generated: $(Join-Path $outputPath "signing-readiness-report.json")"
}
finally {
    Pop-Location
}
